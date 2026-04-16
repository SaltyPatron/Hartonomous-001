namespace Hartonomous.Cli.Migrations;

internal sealed record AppliedMigration(int Version, string Name, string Checksum);
