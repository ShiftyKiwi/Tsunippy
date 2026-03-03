# Release Guide

Versioning for this fork should stay aligned across all three files:

- `Tsunippy.csproj`: `1.0.2.0`
- `Tsunippy.json`: `AssemblyVersion`
- `pluginmaster.json`: `AssemblyVersion`, release URLs, changelog, and `LastUpdate`

Tag and release naming:

- Git tag: `v1.0.2`
- GitHub release title: `v1.0.2`
- Release asset: `latest.zip`

Build environment:

- GitHub-hosted runners are not enough for this project as-is.
- `Dalamud.NET.Sdk` expects a valid Dalamud dev directory, usually at `%APPDATA%\XIVLauncher\addon\Hooks\dev`.
- The included GitHub Actions release workflow is intended for a `self-hosted` Windows runner with XIVLauncher and Dalamud already installed.
- If your Dalamud dev directory is in a custom location, set `DALAMUD_HOME` on the runner.

`pluginmaster.json` is currently wired to:

- `https://github.com/ShiftyKiwi/Tsunippy/releases/download/v1.0.2/latest.zip`

Release checklist:

1. Bump the version in `Tsunippy.csproj`, `Tsunippy.json`, and `pluginmaster.json`.
2. Update the `pluginmaster.json` changelog text.
3. Update `pluginmaster.json` release URLs to the new tag, for example `v1.0.2`.
4. Update `pluginmaster.json` `LastUpdate` to the current Unix timestamp.
5. Commit and push `main`.
6. Make sure your self-hosted Windows GitHub runner is online and has XIVLauncher/Dalamud installed.
7. Create and push a matching tag, for example `git tag v1.0.2` then `git push origin v1.0.2`.
8. Let GitHub Actions build the Release configuration and attach `bin/Release/Tsunippy/latest.zip` to the GitHub release.
9. Verify the release page contains `latest.zip` and the URL in `pluginmaster.json` downloads correctly.
