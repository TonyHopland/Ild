## What to build

`Program.cs` uses `dbContext.Database.EnsureCreated()` instead of `Migrate()`. `EnsureCreated` creates the schema from the model but does not apply EF Core migrations. On schema changes (like the `RecoveryPolicy` enum type change in #10), `EnsureCreated` will either fail or drop/recreate the database, causing data loss.

Replace `EnsureCreated()` with `Migrate()`. Set up the migration infrastructure (`ILD.Data/Migrations/` folder). Generate the initial migration.

## Acceptance criteria

- [x] `EnsureCreated()` replaced with `Migrate()` in `Program.cs`
- [x] Migration infrastructure set up (`ILD.Data/Migrations/` with design-time factory)
- [x] Initial migration generated and applied successfully
- [x] Database schema matches entity models after migration
- [x] Existing data preserved across migration application

## Blocked by

- Blocked by #10 (Fix data model issues — enum type change requires a migration)
