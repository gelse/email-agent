// M2T1 step 14: one alias so every interface and DTO file can write the short
// name `JsonSchema` unqualified.
//
// M2T1 deviation (recorded in the task file): step 14 asks for
// `using JsonSchema = JsonSchema.Net.Evaluations.JsonSchema;`, but
// JsonSchema.Net publishes its types under the `Json.Schema` namespace — there
// is no `JsonSchema.Net.Evaluations` namespace. The package-version deviation
// (9.6.0 instead of the nonexistent 10.0.0) is recorded in the csproj.
global using JsonSchema = Json.Schema.JsonSchema;
