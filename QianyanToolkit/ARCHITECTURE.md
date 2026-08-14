# QToolKit architecture

- `Plugin` owns Dalamud service injection, `/qtk`, the module hub, and unified configuration.
- `ModuleHost` owns seven independently startable and stoppable modules.
- Each legacy runtime retains its original command and UI while saving into a nested QToolKit configuration section.
- `LegacyMigrationService` imports the six persistent standalone JSON files and records the stateless White Mage module migration.
- `SelfUpdateService` checks QToolKit updates whenever its main window opens, shows a short countdown, and automatically applies the update with an installer fallback.
- Imported source files are read-only and remain available for rollback.
- Discord verification is a future boundary and is not part of this release.
