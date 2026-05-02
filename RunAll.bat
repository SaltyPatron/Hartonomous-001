.\scripts\Docker\Down.ps1 -RemoveVolumes -Confirm 2>&1 > logs\ShutDockerDown.log
.\scripts\Docker\Build.ps1 2>&1 > logs\Build.log
.\scripts\Docker\Up.ps1 -Rebuild 2>&1 > logs\BringDockerUp.log
.\scripts\db\Drop.ps1 -Force 2>&1 > logs\DropDatabase.log
.\scripts\db\Create.ps1 2>&1 > logs\CreateDatabase.log
.\scripts\db\Migrate.ps1 -Action up 2>&1 > logs\MigrateDatabase.log
.\scripts\seed\Ucd.ps1 -SourceRoot D:\Models 2>&1 > logs\SeedUCDUCA.log
.\scripts\seed\Iso639.ps1 -SourceRoot D:\Models 2>&1 > logs\SeedISO639.log
.\scripts\seed\WordNetOmw.ps1 -SourceRoot D:\Models 2>&1 > logs\SeedWordnetOMW.log
.\scripts\seed\UniversalDeps.ps1 -SourceRoot D:\Models 2>&1 > logs\SeedUD.log
.\scripts\seed\Wiktionary.ps1 -SourceRoot D:\Models 2>&1 > logs\SeedWiktionary.log
.\scripts\seed\Tatoeba.ps1 -SourceRoot D:\Models 2>&1 > logs\SeedTatoeba.log