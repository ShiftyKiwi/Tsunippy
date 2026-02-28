# Release Guide

Versioning for this fork should stay aligned across all three files:

- `Tsunippy.csproj`: `1.0.1.0`
- `Tsunippy.json`: `AssemblyVersion`
- `pluginmaster.json`: `AssemblyVersion`, release URLs, changelog, and `LastUpdate`

Tag and release naming:

- Git tag: `v1.0.1`
- GitHub release title: `v1.0.1`
- Release asset: `latest.zip`

`pluginmaster.json` is currently wired to:

- `https://github.com/ShiftyKiwi/Tsunippy/releases/download/v1.0.1/latest.zip`

Release checklist:

1. Bump the version in `Tsunippy.csproj`, `Tsunippy.json`, and `pluginmaster.json`.
2. Update the `pluginmaster.json` changelog text.
3. Update `pluginmaster.json` release URLs to the new tag, for example `v1.0.2`.
4. Update `pluginmaster.json` `LastUpdate` to the current Unix timestamp.
5. Commit and push `main`.
6. Create and push a matching tag, for example `git tag v1.0.1` then `git push origin v1.0.1`.
7. Let GitHub Actions build the Release configuration and attach `latest.zip` to the GitHub release.
8. Verify the release page contains `latest.zip` and the URL in `pluginmaster.json` downloads correctly.

