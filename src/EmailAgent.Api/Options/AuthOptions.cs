// M1T4: AuthOptions moved to EmailAgent.Core.Options so Infrastructure
// (RefreshTokenStore's RefreshTokenTtl) does not need a project reference
// back into Api. This file is a thin alias kept only so any lingering
// EmailAgent.Api.Options.AuthOptions references still compile during the
// transition; prefer EmailAgent.Core.Options.AuthOptions.
global using AuthOptions = EmailAgent.Core.Options.AuthOptions;
