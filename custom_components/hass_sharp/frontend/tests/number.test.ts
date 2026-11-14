import { expect, it } from 'vitest'
import { decimalSeparator, numberMessage, parseNumber } from '../src/visual/widgets/number'
import { NumberFormat } from '../src/types'

const browser = new Intl.NumberFormat().formatToParts(1.5).find(p => p.type === 'decimal')!.value

it('takes the decimal separator from the number format as Home Assistant resolves it', () => {
  const of = (number_format: NumberFormat | string | undefined, language = 'en') => decimalSeparator({ number_format: number_format as NumberFormat, language })
  expect(of(NumberFormat.comma_decimal)).toBe('.')
  expect(of(NumberFormat.none)).toBe('.')
  expect(of(NumberFormat.decimal_comma)).toBe(',')
  expect(of(NumberFormat.space_comma)).toBe(',')
  expect(of(NumberFormat.language, 'de')).toBe(',')
  expect(of(NumberFormat.language, 'fr')).toBe(',')
  expect(of(NumberFormat.language, 'en')).toBe('.')
  expect(of(NumberFormat.language, 'en-GB')).toBe('.')
  expect(of(NumberFormat.system)).toBe(browser)
  // Not readable: unknown.
  expect(of(undefined)).toBeUndefined()
  expect(of('something_new')).toBeUndefined()
  expect(of(NumberFormat.language, 'not a language tag!')).toBeUndefined()
  expect(decimalSeparator(undefined)).toBeUndefined()
})

it.each([
  // [text, point format, comma format, format unknown]
  ['21,5', NaN, 21.5, 21.5],
  ['1,000', NaN, 1, NaN],
  ['-12,345', NaN, -12.345, NaN],
  ['1,0005', NaN, 1.0005, 1.0005],
  ['1,5e3', NaN, 1500, 1500],
  [',5', NaN, 0.5, 0.5],
  ['1.5', 1.5, 1.5, 1.5],
  ['1000', 1000, 1000, 1000],
  ['1,2,3', NaN, NaN, NaN],
  ['1.2,3', NaN, NaN, NaN],
  ['1.000,5', NaN, NaN, NaN],
  ['abc', NaN, NaN, NaN],
  ['', undefined, undefined, undefined],
  ['  ', undefined, undefined, undefined],
] as const)('reads %j as %s with a point, %s with a comma, %s when the format is unknown', (text, point, comma, unknown) => {
  expect(parseNumber(text, '.')).toBe(point)
  expect(parseNumber(text, ',')).toBe(comma)
  expect(parseNumber(text)).toBe(unknown)
})

it('says to use a point where a comma was the problem, and only there', () => {
  expect(numberMessage('1,000', '.')).toBe('Enter a number. Use a point for decimals.')
  expect(numberMessage('1,000', undefined)).toBe('Enter a number. Use a point for decimals.')
  expect(numberMessage('1,5', '.', 'Enter a whole number.')).toBe('Enter a whole number. Use a point for decimals.')
  expect(numberMessage('abc', '.')).toBe('Enter a number.')
  expect(numberMessage('1,2,3', '.')).toBe('Enter a number.')
  expect(numberMessage('1,2', ',')).toBe('Enter a number.')
})
