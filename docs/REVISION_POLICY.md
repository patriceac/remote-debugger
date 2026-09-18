# Revision policy

`Directory.Build.props` is the single source of truth for the product revision. The patch number increments for each delivered bug fix or feature build; the installer and Release executable use that same value. `scripts/Build.ps1` reads the project revision automatically unless an explicit `-Version` is supplied for a controlled fixture build.

Every delivered revision is reported with its product version, Release SHA-256, installer SHA-256, and Git commit. A remote computer is synchronized to the same signed revision before normal support operations continue.
