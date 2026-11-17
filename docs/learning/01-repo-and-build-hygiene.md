# Step 01 — Repo and build hygiene

> **Topic:** how a multi-project .NET repo keeps every project consistent: one SDK version, one place for shared build settings, one place for package versions, and analyzers that fail the build instead of being ignored.
> **Tag when approved:** `step-01-repo-and-build-hygiene`

## Why this matters for this project
This repo will grow to about 25 .NET projects (10 services, some with 4 layers, plus tests and building blocks). Without central rules:
- Each project drifts: different package versions, nullable on in some and off in others, different C# versions.
- "Works on my machine" bugs appear when CI uses a different SDK.
- Upgrading a package means editing 15 `.csproj` files.

This step sets up the rules once, before there's any code to drift.

## What was added
| File | Role |
|---|---|
| [global.json](../../global.json) | Pins the .NET SDK used to build (10.0.401, roll forward within the 10.0 line). |
| [FoodDelivery.slnx](../../FoodDelivery.slnx) | Solution in the XML format; `dotnet new sln` creates `.slnx` by default on .NET 10. |
| [Directory.Build.props](../../Directory.Build.props) | Shared MSBuild properties for every project: framework, nullable, analyzers, warnings as errors. |
| [Directory.Packages.props](../../Directory.Packages.props) | Central Package Management. All NuGet versions live here (empty for now). |
| [.editorconfig](../../.editorconfig) | Code style and naming rules, enforced in the build. |
| [.gitattributes](../../.gitattributes) | Line-ending normalisation. |
| [.gitignore](../../.gitignore) | .NET template plus secrets, Node and Vite output. |
| [FoodDelivery.Contracts.csproj](../../src/BuildingBlocks/FoodDelivery.Contracts/FoodDelivery.Contracts.csproj) | Empty project. It holds integration events from step 26; here it exists so the build rules have something to apply to. |
| [CLAUDE.md](../../CLAUDE.md), [docs/roadmap.md](../roadmap.md) | Updated spec and the step roadmap. |

---

## Concepts

### 1. `global.json`: which SDK builds the code
The **SDK** (the compiler, MSBuild and `dotnet` CLI commands) and the **target framework** (the runtime your app runs on) are separate:
- `global.json` picks the SDK: `10.0.401`.
- `<TargetFramework>net10.0</TargetFramework>` picks the runtime. An SDK 10.0.4xx can also build `net8.0` projects.

SDK versions read as `major.minor.FeatureBandPatch`. In `10.0.401`, `4` is the **feature band** (4xx) and `01` is the patch.

`rollForward` decides what happens when the exact version isn't installed:

| Value | Accepts |
|---|---|
| `disable` | exactly 10.0.401 |
| `patch` | 10.0.401–10.0.499 (same band) |
| `latestFeature` (**ours**) | the highest installed 10.0.x that's ≥ 10.0.401, including newer bands like 10.0.5xx |
| `latestMajor` | anything newer, including 11.x |

We use `latestFeature`: new feature bands (new analyzers, CLI features) are picked up, but never a new major SDK by surprise.

### 2. `Directory.Build.props`: shared build settings
MSBuild walks up from each `.csproj` and imports the **first** `Directory.Build.props` it finds.

**Import order:**
1. `Directory.Build.props` is imported *early*, by `Microsoft.Common.props`, before the project's own content.
2. The project file is read.
3. `Directory.Build.targets` is imported *late*.

So a `.csproj` can override anything set in the props (experiment 4 shows this).

**Use `.props` for defaults and `.targets` for logic** that needs final values, such as targets that read `$(TargetFramework)` after the project set it.

**It doesn't chain automatically.** If `tests/Directory.Build.props` exists (we'll add one in step 06 to relax some analyzer rules for test projects), the root file is not imported unless the nested file imports it explicitly:
```xml
<Import Project="$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))" />
```

**What ours sets:**
- `net10.0`, `LangVersion latest`, `Nullable`, `ImplicitUsings`. The `.csproj` files become nearly empty.
- `AnalysisLevel=latest` + `AnalysisMode=Recommended`: turns on the "Recommended" set of **CA rules** (code quality: security, performance, reliability). These analyzers ship inside the SDK, so no package is needed.
- `EnforceCodeStyleInBuild=true`: **IDE rules** (style, from `.editorconfig`) normally only show in the editor. This makes them run during `dotnet build`, and therefore in CI.
- `TreatWarningsAsErrors=true`: a warning nobody is forced to fix gets ignored. Exception: `NU1901`/`NU1902` (low/moderate vulnerability advisories) stay warnings, so a newly published advisory can't block your local build at random. High and critical ones still fail it.
- `GenerateDocumentationFile=true` + `NoWarn CS1591`: a workaround, see gotcha A.
- `Deterministic` + `ContinuousIntegrationBuild` (when `CI=true`): the same source produces byte-identical output, and PDBs hold normalised paths instead of `D:\FoodDelivery\...`.

### 3. Central Package Management (CPM)
`Directory.Packages.props` declares `<PackageVersion Include="X" Version="1.2.3" />` once. Projects write `<PackageReference Include="X" />` with **no version**.
- A version in a `.csproj` → **NU1008**.
- A reference without a central version → **NU1010**.
- Upgrading a package = one line in one file, and every service gets the same version, so there's no "Identity runs EF 10.0.1, Order runs 10.0.3".
- `CentralPackageTransitivePinningEnabled`: if a package listed centrally also comes in **indirectly** (a dependency of a dependency), the central version wins. That's how you force a patched version of a vulnerable transitive package without waiting for the library that pulls it in.
- Escape hatch: `<PackageReference Include="X" VersionOverride="..." />` for one project. It can be turned off with `CentralPackageVersionOverrideEnabled=false`.
- `GlobalPackageReference` (not used yet) adds a package, such as an analyzer, to every project.

### 4. `.editorconfig`: style as a build rule
- **CA rules** = "is this code correct, safe and fast?" **IDE rules** = "does it look the way we agreed?" Both end up as build errors here.
- Rules chosen: file-scoped namespaces, usings outside the namespace, `_camelCase` private fields, `I`-prefixed interfaces, readonly fields, no unused usings or private members. Preferences like `var` are only `suggestion`, so they never break the build.
- Files in `Migrations/` are marked `generated_code = true`, so EF's generated code isn't style-checked.

**Two gotchas, both hit and fixed while building this step:**
- **A. IDE0005 (unused usings) is silent in a command-line build** unless an XML documentation file is generated. That's an implementation detail of the analyzer. Hence `GenerateDocumentationFile=true`, plus `NoWarn CS1591` so we aren't forced to write `///` comments on every public member.
- **B. Naming rules didn't fail the build at first.** A rule's `.severity = warning` is honoured by the editor, but `dotnet build` only enforces naming when `dotnet_diagnostic.IDE1006.severity` is set explicitly. Experiment 3 caught this: a field called `count` passed until that line was added.

### 5. `.slnx`
The old `.sln` format is GUID-heavy and causes merge conflicts. `.slnx` is plain XML listing project paths and folders. Your Jira project already uses it.

### 6. `.gitattributes`
Windows (CRLF) vs Linux (LF) line endings: `* text=auto` stores LF in the repo and converts on checkout. `.sh` files are forced to LF because they run on Linux CI runners and in containers, where a CRLF shebang breaks with `/bin/bash^M: bad interpreter`.

---

## Trade-offs and alternatives
| Choice | Alternative | Why we didn't |
|---|---|---|
| Warnings as errors everywhere | Only in CI (`Condition="'$(CI)'=='true'"`) | Problems should surface on your machine first, not in a red pipeline later. The cost: slightly more friction during experiments. |
| `AnalysisMode=Recommended` | `All` or `Default` | `All` is very noisy (e.g. CA2007 ConfigureAwait everywhere); `Default` misses useful rules. We can tune individual rules in `.editorconfig`. |
| SDK analyzers only | StyleCop.Analyzers, Roslynator, SonarAnalyzer | They add many overlapping opinions. Not worth the noise for learning; can be added later through `GlobalPackageReference`. |
| CPM | NuGet lock files (`RestorePackagesWithLockFile`) | They solve different problems: CPM = one version per package across projects; lock files = exact transitive graph per restore. They can be combined later if reproducible restores matter (e.g. step 57 images). |
| Monorepo (all services in one repo) | One repo per service | One developer, shared contracts, one CI and atomic changes across services. The trade-off is weaker enforcement of service independence, which step 06 adds back with an architecture test. |
| `latestFeature` roll-forward | `patch` or `disable` | `disable` breaks the build every time the SDK updates on the machine; `patch` blocks new feature bands. |

---

## Break-it experiments
All five were run while building this step; the outputs below are real. Undo each change afterwards (`git checkout -- <file>` or delete the file).

### 1. Put a version in a project file
Add this to `FoodDelivery.Contracts.csproj`:
```xml
<ItemGroup><PackageReference Include="Humanizer.Core" Version="2.14.1" /></ItemGroup>
```
`dotnet build` →
```
error NU1008: The following PackageReference items cannot define a value for Version: Humanizer.Core.
Projects using Central Package Management must define a Version value on a PackageVersion item.
```

### 2. Reference a package that has no central version
Same as above but without `Version="..."` →
```
error NU1010: The following PackageReference items do not define a corresponding PackageVersion item: Humanizer.Core.
```
Fix: add `<PackageVersion Include="Humanizer.Core" Version="2.14.1" />` to `Directory.Packages.props`.

### 3. Break the style rules
Create `src/BuildingBlocks/FoodDelivery.Contracts/Probe.cs`:
```csharp
using System.Text;
namespace FoodDelivery.Contracts
{
    public class Probe
    {
        private int count;
        public int Next() => ++count;
    }
}
```
`dotnet build` →
```
Probe.cs(1,1):  error IDE0005: Using directive is unnecessary.
Probe.cs(2,11): error IDE0161: Convert to file-scoped namespace
Probe.cs(6,21): error IDE1006: Naming rule violation: Missing prefix: '_'
```
Then comment out `dotnet_diagnostic.IDE1006.severity = warning` in `.editorconfig` and build again. Only the first two errors remain. The naming problem still shows in VS Code, but the build no longer catches it (gotcha B, which is how it was found).

### 4. Override a shared property in one project
```powershell
dotnet msbuild src\BuildingBlocks\FoodDelivery.Contracts -getProperty:Nullable   # → enable  (from Directory.Build.props)
```
Add `<PropertyGroup><Nullable>disable</Nullable></PropertyGroup>` to the `.csproj` and run it again → `disable`. That's the import order in action: the props are imported before the project body, so the project wins.
To see the fully merged project (every imported file inlined): `dotnet msbuild <csproj> -preprocess:merged.xml`, then search `merged.xml` for `Directory.Build.props`.

### 5. Pin an SDK you don't have
Change `global.json` to `"version": "10.0.999"`, then run `dotnet --version` → fails with *"A compatible .NET SDK was not found"*. Every `dotnet` command in this folder is blocked; outside the folder it still works. This is exactly what happens on a CI runner without the right SDK, which is why step 07 installs the SDK from `global.json`.

---

## Interview questions

**1. What's the difference between `Directory.Build.props` and `Directory.Build.targets`?**
Both are auto-imported by MSBuild from the nearest parent folder. `.props` is imported early (before the project content), so it's for **defaults** that projects can override. `.targets` is imported late (after the project), so it's for **logic that needs final property values** or must run after the project's own settings: custom targets, or properties computed from `$(TargetFramework)`. Neither chains to a parent file automatically; you import the parent explicitly with `GetPathOfFileAbove`.

**2. What problem does Central Package Management solve, and how?**
Version drift across projects. With 20+ projects, the same package ends up at different versions, causing runtime binding issues, inconsistent behaviour and painful upgrades. CPM puts `<PackageVersion>` items in `Directory.Packages.props` and forbids `Version` on `<PackageReference>` (NU1008). One edit upgrades everything. `VersionOverride` is the per-project escape hatch, and transitive pinning lets the central file force versions of indirect dependencies.

**3. A transitive dependency has a known vulnerability. How do you fix it without waiting for the library that pulls it in?**
NuGet audit reports it (NU1901–NU1904; `NuGetAuditMode=all` includes transitive packages). Fix by **pinning** a patched version: with CPM and `CentralPackageTransitivePinningEnabled=true`, add a `<PackageVersion>` for the vulnerable package and the central version wins over the transitive one. Without CPM, add a direct `PackageReference` to the patched version. Then check the parent library still works with it; that's a compatibility risk you now own.

**4. What's the difference between the SDK version in `global.json` and `<TargetFramework>`?**
`global.json` picks the **build tooling** (compiler, MSBuild, CLI) used in that folder. `TargetFramework` picks the **runtime and API surface** the output runs on. A new SDK can build older targets (SDK 10 builds `net8.0`), but not the other way round. `rollForward` controls how strictly the SDK version is matched.

**5. CA rules vs IDE rules, and how do you make sure both are enforced on every PR?**
CA (code analysis) rules are about correctness, security, performance and reliability, e.g. CA2100 SQL injection or CA1849 sync-over-async. IDE rules are about style and naming. CA rules run in the build by default (scope set by `AnalysisLevel`/`AnalysisMode`). IDE rules need `EnforceCodeStyleInBuild=true`. With `TreatWarningsAsErrors`, both fail the CI build. `dotnet format --verify-no-changes` is another check for formatting-only issues.

**6. Arguments for and against `TreatWarningsAsErrors`?**
For: warnings that don't break anything get ignored and pile up until real problems hide among hundreds of them; zero-warning builds keep the signal clean. Against: friction while experimenting, and builds can break when tooling updates add new warnings (a new analyzer level, a new vulnerability advisory). Mitigations: `WarningsNotAsErrors` for specific codes (we exempt low/moderate NuGet advisories), pinning `AnalysisLevel` to a fixed version instead of `latest` if stability matters more than new rules, or enabling it only in CI.

**7. What are deterministic builds and why does `ContinuousIntegrationBuild` matter?**
Deterministic: the same inputs produce byte-identical outputs (no timestamps or random GUIDs in the assembly), which makes caching, verification and reproducing a shipped build possible. `ContinuousIntegrationBuild=true` also normalises file paths embedded in PDBs (`/_/src/...` instead of `D:\FoodDelivery\src\...`), so builds on different machines match and Source Link can map them to the repo. It should only be on in CI, because it can hurt local debugging.

**8. Monorepo or one repo per microservice?**
Monorepo: atomic changes across services and contracts, one CI setup, easy refactoring and shared tooling; risks are accidental coupling (a service referencing another directly) and slower CI without per-path filtering. Polyrepo: strong independence and per-team ownership; costs are versioning shared contracts as packages, coordinating cross-service changes, and duplicated tooling. For a small team, a monorepo with **enforced boundaries** (architecture tests, no project references between services) is usually the pragmatic choice. That's what we do.

**9. Why add a `.gitattributes` file?**
To make line endings deterministic regardless of each developer's `core.autocrlf` setting. Mixed CRLF/LF causes whole-file diffs and broken shell scripts in Linux containers (`bad interpreter: /bin/bash^M`). `* text=auto` normalises to LF in the repo, and specific patterns (`*.sh eol=lf`) override where it matters.

---

## Machine setup (you run these once)
These change your machine, not the repo, so run them yourself.

**1. Cap WSL2 (Docker Desktop's VM) at 6 GB.** Create `%USERPROFILE%\.wslconfig`:
```ini
[wsl2]
memory=6GB
processors=4
swap=2GB
```
Then quit Docker Desktop and run `wsl --shutdown` in PowerShell. Start Docker Desktop again. To check: once a container is running later, `wsl -d docker-desktop free -h` shows about 6 GB total.

**2. Enable pnpm** (Node 24 still ships Corepack):
```powershell
corepack enable
corepack prepare pnpm@latest --activate
pnpm --version
```
If `corepack enable` fails with a permissions error, run that one line in an admin PowerShell.

---

## Review checklist
- [ ] `dotnet build FoodDelivery.slnx` → 0 warnings, 0 errors.
- [ ] Experiments 1–5 reproduced.
- [ ] You can explain props vs targets import order, and what NU1008 and NU1010 mean.
- [ ] Machine setup done (`.wslconfig`, pnpm).
- [ ] Read [CLAUDE.md](../../CLAUDE.md) and [docs/roadmap.md](../roadmap.md) and agree with them.
- [ ] Approved, then commit + tag `step-01-repo-and-build-hygiene`.
