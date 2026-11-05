# Repository Working Rules

## Read this first
Before doing **any work** in this repository—analysis, planning, code changes, reviews, debugging, documentation, or release tasks—read this file first and follow its rules.

## Required workflow
1. Read this file before inspecting or changing anything else.
2. Inspect the current implementation and relevant project conventions before proposing or making changes. Do not assume previous chat context is more current than the repository.
3. Fetch the latest version of every file before editing it. For updates, use the freshly fetched blob SHA.
4. Keep changes focused and consistent with the existing architecture. Do not introduce unnecessary abstractions, dependencies, or broad refactors.
5. After editing, fetch the changed files again and verify that the committed contents match the intended changes.
6. Run the relevant build and tests when the available environment permits. Clearly report anything that was not run; never claim a build or test passed without actually running it.
7. Use a feature branch and pull request for repository changes unless the user explicitly asks for another workflow.
8. **Always merge every pull request you create into `master` after creating it.** Do not leave your PRs open. Use the repository's normal merge method and verify the merge succeeded. Only stop if merging is blocked by failed required checks, conflicts, missing permissions, or another GitHub restriction; explain the blocker clearly. If the user explicitly asks to keep a PR open, follow that instruction instead.

## Versioning is mandatory
- Whenever a change modifies the behavior, functionality, or deliverables of a packaged tool or application, review its version before finishing.
- Bump the version for every such release-worthy change. Do not forget version bumps just because the code change is small or documentation accompanies it.
- For .NET tools, update the authoritative `<Version>` in the project file (and any other version declarations that the project actually uses) according to the existing versioning scheme.
- Keep package/release documentation and version references consistent where applicable.
- Pure typo fixes or internal-only documentation changes that do not change a release artifact do not automatically require a version bump. When uncertain, ask rather than silently skipping it.

## Completion report
Summarize the changes, version impact, verification performed, and any remaining manual steps. Include the pull request link when a PR is created.
