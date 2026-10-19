# Recommended host-authorized command sequencing over Photon

Status: design recommendation. Version 0.5.0 restores native Photon gameplay and adds no enforcement protocol. This proposal needs a separately audited, opt-in implementation and live tests before use with a campaign.

## Scope: executable actions only

Apply host authority only to the executable gameplay actions represented in the action/combat log, such as audited attack, spell/power and reaction executions. Cursor/pointer updates and character movement remain on the game's existing Photon paths and do not require a proposal, host-issued action sequence or additional host approval. Other RPCs stay outside this protocol unless explicitly classified as an in-scope executable action. Diagnostic RPC log entries are not the definition of this scope.

Classify by the underlying action type before execution, not by watching rendered log text after an action has already happened. The installed game has separate MoveCharacterRPC and generic ExecuteActionRPC/ExecuteActionChainRPC/ExecuteInstantSingleActionRPC entry points. Audit the action payload: a generic execution entry point can contain an excluded movement action or mixed chain, so RPC names alone are insufficient. Do not indiscriminately redirect every generic action chain or alter its internal dependencies. Independent AI/triggered executions in scope need the same ordering as player-requested actions; child effects belonging to an already committed action must not be executed a second time through a second commit.

Movement remains synchronized through Photon; it is not treated as purely cosmetic. An authoritative action must refer to an agreed logical position/movement-completion boundary when position affects its preconditions. Defer that action until the referenced movement state is ready, rather than adding host authorization to every movement update. If movement triggers a separate executable reaction or other in-scope action, sequence that execution while leaving the movement message itself on the native path.

## Position prerequisite before each executable action

The host's committed action includes the acting character's stable ID, location/scene ID and expected origin tile `(x, y, z)`. Capture that expected origin when the host commits the action; do not substitute wherever the host's character has moved by the time another client receives it. The expected origin is where the action starts, not the action's target/destination tile.

Before a peer executes the action:

1. Resolve the same character in the same location and compare its logical grid tile with the committed expected origin. Do not compare animated transforms or an interpolated position.
2. If the character is already on that tile and no in-flight movement will carry it away before the action begins, satisfy the position prerequisite.
3. Otherwise inspect the character's current execution chain and pending executable chains for a valid, uncompleted move to that exact tile in this location/session. If a matching move is already running, await it. If it is queued, run/reuse that move before the action, preserving the move's own prerequisites and completion callbacks. Remove/mark its original queue occurrence so it cannot execute twice.
4. If no matching move exists, insert a move to the host's expected tile as a prerequisite of this action, using the game's normal movement/action machinery and Photon synchronization. Keep one repair dependency ID derived from the session, character and parent action sequence so repeated messages cannot create repeated repair moves.
5. Wait for the selected or inserted move to finish and then recheck the actual logical tile. Scheduling a move or receiving its RPC is not completion: the inspected MoveCharacterAsync hands work to GameLocationActionManager and returns. Use the movement/chain completion boundary plus the tile check.
6. Only acknowledge the action as ready once the position prerequisite is satisfied. Release the original action through its normal host-authorized execution barrier, without an intervening move for this character. Peers already at the expected tile perform a no-op for the repair rather than moving again.

An in-flight move to a different destination must be resolved through the game's normal interruption/dependency rules, not overlapped with another move. If movement fails, the actor/location is unavailable, the tile cannot be reached, or the tile still differs after completion, keep the action blocked and report recovery failure. Do not execute it from the wrong tile or silently teleport through an invalid destination. The protocol must support deferring/reissuing the action's execution barrier after repair; it must not execute a now-expired tick late.

This is a position-repair prerequisite for an executable action, not general host approval of cursor or ordinary movement messages. Repairs are host-identified and idempotent across the action stream. A peer that already applied a move must not pay movement costs or apply traps, opportunity attacks or other committed effects a second time. Mapping the repair onto Solasta's movement budget, pathfinding and reaction machinery needs live auditing before implementation; merely calling MoveCharacter again is not a validated desync repair.

## Separate host identity, order and simulation agreement

Photon's current room master is the authoritative host identity for the protocol. Check Photon-supplied `EventData.Sender` or `PhotonMessageInfo.Sender.ActorNumber`, not a host ID claimed inside a message payload. Pin that actor together with a fresh session epoch. Revalidate against the room master; if it changes during gameplay, pause rather than silently continuing the previous stream under a different host. Resume only after an explicit new epoch and agreed checkpoint.

Receiving a command from the right host does not prove it was applied at the right time, and common command order does not guarantee deterministic game behavior. Track these properties separately.

## Proposed message flow

1. Each player sends an in-scope executable-action proposal to the room master through Photon. Cursor and character-movement messages bypass this queue and continue normally. Include a unique request ID; the host checks the actual sender and their character/control permissions. Do not simply reject all non-host RPCs in the existing game: Solasta legitimately sends player-originated commands, and doing so would break play.
2. The host validates a proposal, assigns a monotonically increasing sequence and a host-selected logical execution tick/batch, and broadcasts a committed envelope through Photon. Include the session epoch, pinned host actor, sequence, originating player/request ID, character/location IDs, expected origin tile, command kind, recipients, payload and payload hash. Preserve the original player identity for callbacks/permissions; the commit's transport sender remains the host.
3. All peers, including the host, wait for that committed envelope. The host must not execute through an immediate local shortcut. Use reliable Photon delivery with server-mediated ordering, such as AllViaServer or an equivalent RaiseEvent route to all, for the committed stream. Use one ordered control path for commits; do not rely on order across unrelated channels, properties or local callbacks.
4. A receiver accepts commits only from the Photon-reported current/pinned host and current epoch. It queues by sequence, ignores an identical duplicate, and rejects a duplicate with different contents. A gap blocks execution and requests the missing range from a bounded host log; never skip a missing entry or execute a later command opportunistically.
5. Preserve a contiguous stream for every peer even for targeted commands. Send all peers the sequencing envelope, with non-recipients recording a no-op, so targeted delivery does not resemble a missing sequence.

Photon documents that ViaServer avoids the immediate local execution shortcut and provides common receiver order. That is a foundation for the committed stream, not an acknowledgment that an action has finished in Solasta. See [RPC targets and ordering](https://doc.photonengine.com/pun/current/gameplay/rpcsandraiseevent/).

## Enforce execution, not just receipt

Integrate at the actual executable-action scheduler and relevant simulation boundaries, preserving the native cursor/movement handlers. Each peer must apply a committed action batch at the same logical tick and preserve its host-issued sequence within that batch. An action received after its execution boundary must trigger a pause/recovery path, not late execution against a different state.

Use a readiness/commit barrier at audited gameplay-safe boundaries: peers acknowledge the staged batch and prerequisites, then the host authorizes that logical step. Acknowledgments should distinguish received, staged and completed execution watermarks. Maintain network processing while paused. Merely skipping NetworkingManager.FixedUpdate is not proof that all other gameplay coroutines stop.

Audit nested/local actions, AI, reactions, coroutine scheduling and state-changing room properties that could bypass this queue. Control traffic such as tick notifications, pings and acknowledgments requires explicit handling; do not blindly convert every existing RPC into an authoritative action. Modded party sizes remain determined by the game/UB, with a dynamic roster rather than a hard-coded player maximum.

## Detect the first split and recover safely

At agreed post-execution checkpoints, compare canonical gameplay hashes for the same epoch and completed action sequence/tick. Include stable-ID-sorted entities, HP, logical positions, relevant conditions/inventory and deterministic random state; exclude presentation, animation and wall-clock fields. Compare position-dependent state at the same referenced movement boundary, not while native movement is at different intermediate stages. Hashes taken while an action is still running are not comparable checkpoints.

- Different committed sequence/payload hashes suggest a delivery, targeting or protocol issue.
- Matching received commits but different execution watermarks suggest scheduling/barrier issues.
- Matching executed commands but different checkpoint hashes suggest simulation/RNG divergence or an untracked side effect.

On disagreement, stop advancing, retain the first differing command/checkpoint and perform an audited host snapshot restore or save/reload. Do not replay arbitrary actions into a partially diverged simulation or assume that resending a command cannot duplicate effects. Recovery and host migration require their own design.

The tradeoff is additional host processing, Photon traffic and potentially another message round trip/barrier latency. It enforces host authority and a common command schedule; it cannot by itself repair non-deterministic simulation code. It is an application protocol carried through normal Photon connections, not a direct host socket.
