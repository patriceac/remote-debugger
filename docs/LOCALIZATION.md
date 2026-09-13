# Interface languages

Remote Debugger selects its interface language when the GUI starts. It uses the Windows user's display language, exposed by [.NET CurrentUICulture](https://learn.microsoft.com/en-us/dotnet/api/system.globalization.cultureinfo.currentuiculture?view=net-8.0). French and Spanish regional variants select the corresponding translation; English and all unsupported languages use English. Each PC selects its own language independently. Restart the app after changing the Windows display language.

| Windows language | Interface |
| --- | --- |
| French, including `fr-FR` and `fr-CA` | French |
| Spanish, including `es-ES` and `es-MX` | Spanish |
| English, including `en-US` and `en-GB` | English |
| Other or invariant language | English |

The installer also detects the Windows display language and falls back to English without displaying a language-selection dialog. Numbers, dates and times in the app retain Windows regional formatting. Native Windows file dialogs follow Windows' installed language resources.

For troubleshooting or translation review, the optional GUI launch argument `--ui-language fr-CA`, `--ui-language es-MX` or `--ui-language en-GB` overrides the language for that launch. Unsupported or invalid override values fall back to English. Ordinary launches require no argument. This option is not a saved preference. If the desktop app is already running, another launch restores that instance and keeps its existing language; use Quit first to start in a different language.

The desktop instance identity is shared across executable paths for each Windows user/session. CLI, service and elevated helper entry points run independently. The isolated Lab can represent separate PCs inside one guest only through the existing `--loopback-only` mode with separate explicit data roots. Normal launches share one instance even when different data directories are supplied. A current-user-only local pipe activates the existing workspace; it has no network listener. Run `./scripts/Test-HyperV.ps1 -Role SingleInstance` for concurrent startup, preserved state, tray/foreground restoration, CLI coexistence, normal Quit and crash recovery checks.

Navigation, pairing, session states, countdowns, transfers, validation messages, table captions, accessible field names and tray menus are translated. The product name, diagnostic operation identifiers, CLI syntax, JSON field names, paths and remote diagnostic output retain their original values. Technical exception details supplied by Windows or a remote peer are displayed as received.

## Maintaining translations

`src/RemoteDebugger.Core/Localization/Strings.resx` contains neutral English text. `Strings.fr.resx` and `Strings.es.resx` contain complete translations. ResourceManager supplies neutral English fallback. `UiText.Keys.cs` provides typed properties; keep its keys aligned with the three catalogs. Whole format templates retain numbered placeholders, including their numeric or time format specifiers.

`UiCulture.Initialize` runs before any GUI controls are constructed and sets both the current and default thread UI cultures. It leaves `CurrentCulture` unchanged, preserving regional formatting. CLI and service entry points retain their existing machine-readable contract.

Run `./scripts/Test-Unit.ps1` for language selection, regional variants, fallback, resource completeness, placeholder compatibility, async formatting and presentation tests. Build with `./scripts/Build.ps1 -IncludeLab -Sign`, then run `./scripts/Test-HyperV.ps1 -Role Localization` to inspect the actual signed Release in a disconnected guest. That scenario covers the unmodified system default, French Canadian, Mexican Spanish, British English and German fallback, including the five pages, invalid pairing, connected/paused/ended states and tray menus. Existing French acceptance scenarios pass an explicit French override so their language-specific assertions remain deterministic.
