import { useContext } from 'solid-js'
import { HassCtx } from '../../context'
import type { FrontendLocaleData } from '../../types'

/** A number as JSON writes one (and as stored values are read): digits with an optional point and exponent. */
export const numberText = /^[+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?$/

/** The decimal separator people type, or undefined when it is not known. */
export type DecimalSeparator = '.' | ','

/** The decimal separator a locale writes (`de` → `,`), by `Intl`; undefined when `Intl` cannot say. */
function separatorOf(locale: string | undefined): DecimalSeparator | undefined {
  try {
    const decimal = new Intl.NumberFormat(locale).formatToParts(1.5).find(part => part.type === 'decimal')?.value
    return decimal === ',' ? ',' : decimal === '.' ? '.' : undefined
  } catch {
    return undefined
  }
}

/**
 * The decimal separator of the person's number format in Home Assistant, resolved as Home Assistant's own
 * `numberFormatToLocale` does: `comma_decimal` (1,234.5) and `none` write a point; `decimal_comma` (1.234,5) and
 * `space_comma` (1 234,5) a comma; `language` as `locale.language` writes numbers, `system` as the browser does.
 * Undefined when the format cannot be read.
 */
export function decimalSeparator(locale: Partial<FrontendLocaleData> | undefined): DecimalSeparator | undefined {
  switch (locale?.number_format) {
    case 'comma_decimal': case 'none': return '.'
    case 'decimal_comma': case 'space_comma': return ','
    case 'language': return locale.language ? separatorOf(locale.language) : undefined
    case 'system': return separatorOf(undefined)
    default: return undefined
  }
}

/** The decimal separator of the Home Assistant around the component, read when it is called (a setting can change). */
export function useDecimalSeparator(): () => DecimalSeparator | undefined {
  const hass = useContext(HassCtx)
  return () => {
    try {
      return decimalSeparator(hass?.locale)
    } catch {
      return undefined
    }
  }
}

/** A single comma followed by exactly three digits ("1,000", "-12,345") reads as a thousands separator in some formats. */
const thousands = /,\d{3}(?!\d)/

/**
 * Parse a number a person typed; undefined for empty text, NaN for anything that is not a finite number. A point is
 * always the decimal point (JSON values are pasted). A comma is the decimal point only where it is the person's
 * decimal separator (`decimal`): "21,5" is 21.5 in a comma format and refused in a point format, where "1,000" would
 * otherwise be read as 1. When the format is not known, a comma is read as the decimal point unless it is followed by
 * exactly three digits, which is refused as ambiguous. Two commas, or a comma and a point, are always refused. Every
 * number field reads typed text through this one parser; the value stored is a JSON number either way.
 */
export function parseNumber(text: string, decimal?: DecimalSeparator): number | undefined {
  let trimmed = text.trim()
  if (trimmed === '') return undefined
  const commas = trimmed.split(',').length - 1
  if (commas > 1 || (commas === 1 && trimmed.includes('.'))) return Number.NaN
  if (commas === 1) {
    if (decimal === '.' || (decimal === undefined && thousands.test(trimmed))) return Number.NaN
    trimmed = trimmed.replace(',', '.')
  }
  if (!numberText.test(trimmed)) return Number.NaN
  const value = Number(trimmed)
  return Number.isFinite(value) ? value : Number.NaN
}

/**
 * The message for a number field that refused `text`: `message` ("Enter a number."), and where a comma was the
 * problem (it is not the person's decimal separator, or it is ambiguous), "Use a point for decimals."
 */
export function numberMessage(text: string, decimal: DecimalSeparator | undefined, message = 'Enter a number.'): string {
  const comma = text.includes(',') && !text.includes('.') && text.split(',').length === 2 && decimal !== ','
  return comma && numberText.test(text.trim().replace(',', '.')) ? `${message} Use a point for decimals.` : message
}
