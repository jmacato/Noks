# AGENTS.md instructions

- For quick C# probes or throwaway runners, use single-file .NET with `dotnet run file.cs`.
- Make frequent commits of complete, coherent changes.
- Use scoped commits so that you can isolate performance changes and regressions.
- Do not include unrelated worktree changes in a commit.
- If the user does not request local browser acceptance, do not run browser acceptance.
- Do not use keypad automation to bypass a startup security-code gate. Disable or repair the EEPROM startup-lock policy instead, so that the firmware reaches the idle screen through its own no-code boot path.
