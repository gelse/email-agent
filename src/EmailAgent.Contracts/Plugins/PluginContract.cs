// M2T1 Scope "In" — plugin contract markers.
//
// There are deliberately NO `[assembly: ...]` markers and no runtime types in
// this file: M2T1 ships the contracts only. The plugin loader (M2T3), the
// `plugin.json` on-disk loading rules and the manifest validator all live in
// M2T3; `PluginManifest` (this directory) is the parsed shape they consume.
// The `ConfigSchema` property on every connector interface returns the
// concrete `JsonSchema` type from JsonSchema.Net (referenced by this project —
// see EmailAgent.Contracts.csproj for the version deviation).
