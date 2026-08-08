/**
 * The `server-only` package's `react-server` export, which is an empty module.
 *
 * Vitest resolves the package's `default` condition instead, and that one throws
 * on import by design — it is the error a client bundle is supposed to get. The
 * modules under test are server modules, so this alias gives them the export the
 * server condition would have.
 */
export {};
