# Tsunippy Manual Smoke Test Checklist

Use this local checklist when validating robustness changes in-game.

1. Enable and disable the plugin with `/tsunippy on`, `/tsunippy off`, and `/tsunippy toggle`.
2. Toggle dry run with `/tsunippy dry`; verify diagnostics and logs update while animation locks are not overwritten.
3. Zone between territories and confirm diagnostics shows an epoch reset reason.
4. Start a cast and interrupt it with movement; confirm cast prediction state clears and no stale pending cast remains.
5. Complete a cast; confirm cast-tax learning updates and only one cast owner writes the corrected lock.
6. Use `/tsunippy reset floor` and verify the dynamic floor resets and epoch advances.
7. Use `/tsunippy reset rtt` and verify RTT maturity returns to warm-up.
8. Use `/tsunippy relearn` and verify learned lock and cast-tax data reset locally.
9. Use `/tsunippy export json` and `/tsunippy export csv`; verify files are created under the plugin config export directory.
10. Open `/tsunippy db`; test freeze/unfreeze, single-entry reset, filtered reset confirmation, reset-all confirmation, and learned JSON export.
11. Open `/tsunippy diag` during startup, combat, zoning, and shutdown; verify no negative RTT values are shown and rejected decisions show no accepted formula.
12. In combat, verify stats categories show cast tax, lock-induced clip, and unknown attribution rather than assigning unrelated recent actions.
