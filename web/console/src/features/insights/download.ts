/** Saves bytes the browser already holds as a file, through a short-lived object URL. */
export function saveBlob(filename: string, blob: Blob) {
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = filename;
  document.body.append(a);
  a.click();
  a.remove();
  window.setTimeout(() => URL.revokeObjectURL(url), 1000);
}

/** `<report>-<yyyy-mm-dd>.csv`, the date being today in UTC. */
export function reportFileName(report: string, now = new Date()): string {
  return `${report}-${now.toISOString().slice(0, 10)}.csv`;
}
