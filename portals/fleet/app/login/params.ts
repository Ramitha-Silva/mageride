/**
 * A repeated query parameter is a string array, and every parameter this screen
 * reads (`next`, `error`, `signedOut`) is single-valued — so `?next=/a&next=/b`
 * takes the first rather than becoming `"/a,/b"`, which `safeNextPath` would
 * refuse and nobody would understand.
 *
 * Shared by `/login` and `/signup`, which are the same screen at two addresses.
 */
export function first(value: string | string[] | undefined): string | undefined {
  return Array.isArray(value) ? value[0] : value;
}
