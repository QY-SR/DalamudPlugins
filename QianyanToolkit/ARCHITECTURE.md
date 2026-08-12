# QToolKit architecture

- `Plugin` owns Dalamud service injection, `/qtk`, the module hub, and unified configuration.
- `ModuleHost` owns six independently startable and stoppable modules.
- Each legacy runtime retains its original command and UI while saving into a nested QToolKit configuration section.
- `LegacyMigrationService` imports the five persistent standalone JSON files and records the stateless White Mage module migration.
- Imported source files are read-only and remain available for rollback.
- Discord verification is a future boundary and is not part of this release.
