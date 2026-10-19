import { useEffect, useState } from 'react';

/** `value`, but only after it has stopped changing for `ms`. An empty value passes at once, so a closed picker clears. */
export function useDebounced(value: string, ms: number): string {
  const [out, setOut] = useState(value);
  useEffect(() => {
    const id = setTimeout(() => setOut(value), ms);
    return () => clearTimeout(id);
  }, [value, ms]);
  return value === '' ? '' : out;
}
