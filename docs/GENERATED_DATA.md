# Generated data and OneDrive

The active project may remain inside OneDrive, but disposable captures, reports,
and runtime logs must not be written into `res://`. They belong under:

```text
%APPDATA%\Godot\app_userdata\RingworldTechDemo\generated\
```

This is Godot's `user://generated/` directory. It is local to each development
machine and intentionally does not contain world saves.

OneDrive does not honor `.gitignore`, so moving disposable writes outside its
sync root is the part that prevents future upload/download churn. Existing
legacy evidence is left in place until it has been reviewed.

## Inspect, open, or clean it

From the repository root:

```powershell
pwsh -File tools/manage_generated_data.ps1 -Action Report
pwsh -File tools/manage_generated_data.ps1 -Action Open
pwsh -File tools/manage_generated_data.ps1 -Action Clean
```

`Clean` prompts before deleting generated output. Two separately confirmed
options handle older project-local data:

```powershell
# Close Godot first. The editor reconstructs this cache on its next launch.
pwsh -File tools/manage_generated_data.ps1 -Action Clean -IncludeGodotCache

# Remove legacy artifacts/captures/test output and root logs after review.
pwsh -File tools/manage_generated_data.ps1 -Action Clean -IncludeLegacyRepoOutput
```

Neither option deletes world saves. The conflict archive is deliberately not
considered generated data and is never touched.

## Before changing locations

After reviewing any evidence you still need, close Godot and run the combined
cleanup. This leaves the source/assets in OneDrive and removes only local output,
the reconstructible project cache, and legacy disposable repo output:

```powershell
pwsh -File tools/manage_generated_data.ps1 -Action Clean -IncludeGodotCache -IncludeLegacyRepoOutput
```

The command asks for confirmation per managed folder. Godot rebuilds `.godot`
on the next machine. Copy only a small, deliberately retained result into a
documentation folder if it must travel with the project.

## World saves are different

Persistent worlds remain under `user://worlds`, outside `generated/`, and this
tool never deletes them. They are also outside the OneDrive repo, so moving to
another computer transfers the project but not a save slot. If the same world
must travel too, use a deliberate save export/backup workflow; do not live-sync
the active world directory or hide it behind a folder junction.

## Repository rules

- `.gitignore` keeps generated data and `.godot` out of future Git commits.
- `artifacts/.gdignore` and `captures/.gdignore` stop Godot from importing old
  screenshots into `.godot/imported`.
- A small set of approved evidence may be copied into a dedicated documentation
  folder. Do not promote complete diagnostic sequences.
- Repeated captures should replace or prune their own temporary run output.
