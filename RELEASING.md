# Publishing a release

1. Set the mod version in `src/SolastaNetworkLab/Info.json` and `SolastaNetworkLab.csproj` to the same stable three-part version. Update the guide and release notes. The wire handshake version changes only for transport compatibility changes.
2. Run `./build.ps1 -Restore -Test` against a legally installed, supported copy of Solasta. This project's source does not include game, Photon, Unity, UB or UMM binaries.
3. Commit and push source changes using the required agent identity policy. Do not commit local decompilations, packages, credentials or research checkouts.
4. Create a draft GitHub release tagged `v<version>` at the verified pushed commit. Upload `artifacts/SolastaNetworkLab-<version>.zip`, confirm GitHub's SHA-256 digest matches the local ZIP, then publish it as the latest release. The updater excludes drafts/prereleases and requires the exact ZIP name and digest.
5. Verify the public latest-release endpoint and the package download. `dotnet run --project tests/UpdateChecks -c Release -- --live` checks the published metadata and stages/verifies the asset inside the project without installing it.

The automatic installer accepts exactly the packaged files listed in `ReleaseUpdates.PackageFiles` and `ApplyUpdate.ps1`. Keep those lists synchronized. Adding/changing that file contract can require a manual migration for existing updater versions. The package contains no GitHub credential. A release without a valid asset digest cannot be installed through the in-game updater.
