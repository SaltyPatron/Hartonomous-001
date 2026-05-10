#@echo off
.\scripts\Docker\Down.ps1 -RemoveVolumes -Force *>&1 > logs\ShutDockerDown.log
.\scripts\build\UnicodeTables.ps1               *>&1 > logs\BuildUnicodeTables.log
.\scripts\build\All.ps1                         *>&1 > logs\BuildAll.log
.\scripts\Docker\Build.ps1                      *>&1 > logs\BuildContainer.log
.\scripts\Docker\Up.ps1   -Rebuild              *>&1 > logs\BringDockerUp.log
.\scripts/build/ExtensionSql.ps1                *>&1 > logs\BuildExtensionSql.log
.\scripts\db\Drop.ps1     -Force                *>&1 > logs\DropDatabase.log
.\scripts\db\Create.ps1                         *>&1 > logs\CreateDatabase.log
.\scripts\db\Bootstrap.ps1                      *>&1 > logs\Bootstrap.log
.\scripts\test\Unicode.ps1                      *>&1 > logs\UnicodeDeterminism.log
.\scripts\seed\Ucd.ps1          -SourceRoot D:\Models *>&1 > logs\SeedUCDUCA.log
.\scripts\seed\Iso639.ps1       -SourceRoot D:\Models *>&1 > logs\SeedISO639.log
#.\scripts\seed\WordNetOmw.ps1   -SourceRoot D:\Models *>&1 > logs\SeedWordnetOMW.log
#.\scripts\seed\UniversalDeps.ps1 -SourceRoot D:\Models *>&1 > logs\SeedUD.log
#.\scripts\seed\Wiktionary.ps1   -SourceRoot D:\Models *>&1 > logs\SeedWiktionary.log
#.\scripts\seed\Tatoeba.ps1      -SourceRoot D:\Models *>&1 > logs\SeedTatoeba.log
#rem ── Modality decomposers ────────────────────────────────────────────
#rem TextDecomp: walks D:\Models\test_data\text via UAX#29 and emits
#rem codepoint/grapheme/word/sentence/paragraph/document entities.
#.\scripts\seed\Text.ps1         -SourceRoot D:\Models *>&1 > logs\SeedText.log
#rem ModelDecomp (Safetensors): per-tensor + per-role decomposition of
#rem any HF-cached model under D:\Models\hub\ matching the ModelFilter
#rem allowlist in src\Hartonomous.Cli\appsettings.json.
.\scripts\seed\Safetensors.ps1  -SourceRoot D:\Models *>&1 > logs\SeedSafetensors.log
