cd D:/Repositories/Cursor/Hartonomous;
./scripts/db/Drop.ps1 -Force 2>&1;
./scripts/db/Create.ps1 2>&1;
./scripts/db/Migrate.ps1 -Action up 2>&1;