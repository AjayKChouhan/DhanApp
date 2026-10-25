// Extracts the first array found in a passthrough Dhan API payload, since the
// backend forwards Dhan's raw JSON as-is (shape can vary: a bare array, or an
// object wrapping one).
export function extractArray(payload: unknown): Record<string, unknown>[] {
  if (Array.isArray(payload)) return payload;
  if (payload && typeof payload === 'object') {
    for (const value of Object.values(payload as Record<string, unknown>)) {
      if (Array.isArray(value)) return value;
    }
    return [payload as Record<string, unknown>];
  }
  return [];
}
