# SmartPrint UI localization

SmartPrint UI copy is stored as pre-translated key/value catalogs. Runtime code never calls a translation service and never translates customer content, filenames, IDs, payment references, printed documents, or API diagnostics.

## Supported locales

- `english` — `en`
- `hindi` — `hi`, Devanagari
- `hinglish` — `hi-Latn`, Roman-script Hinglish

The first-use default remains Hinglish. A user's explicit choice is persisted in `smartprint-ui-locale`; older customer/shop/admin/public preference keys are read as migration fallbacks. The selector is generated from the locale registry.

## Add a locale

1. Add a complete key/value catalog in `catalog.ts`, typed as `Record<keyof typeof english, string>`. Keep message keys identical to English.
2. Add that catalog to `uiCatalogs`.
3. Add its display name, native name, BCP 47 language tag, text direction (`ltr` or `rtl`), aliases, English fallback, and default flag to `localeMetadata`.
4. Run `pnpm --dir apps/portal check:i18n` and `pnpm --dir apps/portal typecheck`.

The selector and locale union are derived from the registry. The catalog checker discovers registered catalogs automatically and rejects missing/extra keys, duplicate keys, blank translations, and interpolation-placeholder mismatches.

## Message conventions

- Use semantic names such as `customer.uploadFiles` and `payment.retry`, not English sentences as keys.
- Preserve placeholders exactly, e.g. `customer.welcome = "Welcome to {shopName}"`.
- Keep a message self-contained and contextual; avoid reusing a short label where its meaning differs by screen.
- Add plural variants as `<key>.one`, `<key>.other`, etc. and resolve them with `translateUiPlural`.
- Use `formatUiNumber`, `formatUiDate`, and `formatUiCurrency` for locale-aware display. Do not localize stored values or alter amounts.
- Keep language selection independent from print settings, privacy mode, permissions, payment state, and backend workflow state.
- New locale catalogs must be reviewed by a fluent human before being exposed to customers.
