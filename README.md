# NPMC Launcher

> An unofficial third-party launcher for **Minecraft: Java Edition**.

**Project status: early development.** There is no public release (no downloadable build) yet.
The launcher's own source code **is public** in this repository and is licensed under the
AGPL-3.0 — start from [Repository layout](#repository-layout) and [License](#license).

---

## What NPMC Launcher is

NPMC Launcher is a personal, non-commercial desktop launcher for Minecraft: Java Edition on
Windows, written in C# (.NET 8) with an Avalonia UI.

It is being built to provide:

- **Version installation** — installing and updating Minecraft: Java Edition directly from
  Mojang's official metadata and file endpoints, with an optional user-selectable acceleration
  mirror for users on slow international connections.
- **Instance management** — multiple isolated game directories, so mods, resource packs and
  shader packs never mix between versions.
- **Launch configuration** — custom window size, JVM heap limits, per-instance Java selection
  and additional JVM arguments.
- **Mod, resource pack, shader and data pack discovery** — searching and installing from the
  Modrinth public API (CurseForge with a user-supplied API key), with dependency resolution for
  mods. Data packs are installed into the selected world's `datapacks/` folder, since they only
  apply per world.
- **Microsoft account sign-in** — standard OAuth 2.0 device authorization flow, described below.

## Repository layout

- `src/NPMC.Launcher.App` — Avalonia desktop UI (the launcher window and its view models)
- `src/NPMC.Launcher.Core` — version metadata, rule evaluation, path layout, launch arguments
- `src/NPMC.Launcher.Infrastructure` — downloads, Modrinth / CurseForge, accounts, storage, modpacks
- `src/NPMC.Launcher.Cli` — command-line entry points (probe / install / runtime / launch)
- `tests/` — unit tests for the Core project
- `tools/` — build, publish and signing helpers
- `docs/` — architecture notes and the current session handoff

This is the complete source of the launcher; building `NPMC.Launcher.sln` with the .NET 8 SDK
reproduces the application. Deliberately **not** in version control: build output (`dist/`), the
generated `runtime/`, `libraries/`, `versions/`, `assets/` and `instances/` directories, and any
user data such as accounts, tokens or the CurseForge key (see `.gitignore`).

## Authentication

NPMC Launcher uses a **public client** (native application) Entra application with the
**device authorization grant**. No client secret is used.

The flow is the standard Minecraft authentication chain:

| Step | Endpoint |
| --- | --- |
| 1 | `login.microsoftonline.com/consumers/oauth2/v2.0/devicecode` — scope `XboxLive.signin offline_access` |
| 2 | `POST https://user.auth.xboxlive.com/user/authenticate` |
| 3 | `POST https://xsts.auth.xboxlive.com/xsts/authorize` — RelyingParty `rp://api.minecraftservices.com/` |
| 4 | `POST https://api.minecraftservices.com/authentication/login_with_xbox` |
| 5 | `GET https://api.minecraftservices.com/entitlements/mcstore` — verify game ownership |
| 6 | `GET https://api.minecraftservices.com/minecraft/profile` |

The Microsoft refresh token is stored only on the local machine, encrypted with Windows DPAPI
(`DataProtectionScope.CurrentUser`). Credentials are never transmitted anywhere other than to
Microsoft and Mojang endpoints.

## Compliance position

This project is designed to stay within the Minecraft End User License Agreement and the
Minecraft Usage Guidelines.

**No game content is redistributed.** NPMC Launcher does not bundle, mirror or re-host any
Minecraft game files. Everything is downloaded at runtime from Mojang's authorized endpoints:

- `launchermeta.mojang.com` — version manifest
- `piston-meta.mojang.com` — version metadata and asset indexes
- `piston-data.mojang.com` — client jar and log4j configuration
- `resources.download.minecraft.net` — game assets
- `libraries.minecraft.net` — libraries

The optional acceleration mirror only redirects requests to those same files. It is never a
substitute distribution channel.

**Authentication is not bypassed.** A valid Microsoft sign-in is required to start the game in
its normal mode. A clearly labelled offline mode exists for local single-player use only, and is
never used to connect to online-mode servers.

**No third-party client IDs are reused.** NPMC Launcher uses its own Entra application
registration. It does not copy or spoof the client ID of the official Minecraft Launcher or of
any other third-party launcher.

## Trademarks and affiliation

NPMC Launcher is an independent, unofficial project. It is **not affiliated with, endorsed by,
sponsored by or otherwise connected to Mojang Studios or Microsoft**.

"Minecraft" is a trademark of Mojang Synergies AB. Mojang Studios and Microsoft are not
responsible for this software or for any damage it may cause.

## Tech stack

- C# / .NET 8 (LTS)
- Avalonia 11 (MVVM via CommunityToolkit.Mvvm)
- System.Text.Json, Microsoft.Extensions.Http / DependencyInjection

## License

The launcher's own source code in `src/` is licensed under the
[GNU Affero General Public License v3.0](LICENSE). If you distribute a modified version, or run
one as a network service, the AGPL requires you to make the corresponding source available.

---

*This repository contains the launcher's own source code (AGPL-3.0). It contains no Minecraft game
content: every game file is downloaded at runtime from Mojang's authorized endpoints.*
