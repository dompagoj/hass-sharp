/** A number as JSON writes one (and as stored values are read): digits with an optional point and exponent. */
export const numberText = /^[+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?$/

/**
 * Parse a number a person typed; undefined for empty text, NaN for anything that is not a finite number. A text with
 * one comma and no point reads the comma as the decimal point ("21,5" is 21.5), as people in comma locales write it
 * and their phones' decimal keypads offer; a text with a point and a comma, or two commas, is refused. Every number
 * field reads typed text through this one parser; the value stored is a JSON number either way.
 */
export function parseNumber(text: string): number | undefined {
  let trimmed = text.trim()
  if (trimmed === '') return undefined
  const commas = trimmed.split(',').length - 1
  if (commas > 1 || (commas === 1 && trimmed.includes('.'))) return Number.NaN
  if (commas === 1) trimmed = trimmed.replace(',', '.')
  if (!numberText.test(trimmed)) return Number.NaN
  const value = Number(trimmed)
  return Number.isFinite(value) ? value : Number.NaN
}
