# SmartPrint UI localization

SmartPrint UI copy is stored as pre-translated key/value catalogs. Runtime code never calls a translation service and never translates customer content, filenames, IDs, payment references, printed documents, or API diagnostics.

## Supported locales

- `english` — `en`
- `hindi` — `hi`, Devanagari
- `hinglish` — `hi-Latn`, Roman-script Hinglish

The first-use default remains English; Hindi and Hinglish are available as explicit choices. Hinglish is intentionally code-mixed: familiar technical labels, product names and standard print terms may remain in English while instructions and explanations are translated. A user's explicit choice is persisted in `smartprint-ui-locale`; older customer/shop/admin/public preference keys are read as migration fallbacks. The selector is generated from the locale registry.

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


## Screen-level copy

For screens whose existing copy is not yet represented by a stable message key, use `translateUiText(locale, englishSourceText)`. Its Hindi and Hinglish dictionaries are static key/value maps in `catalog.ts`; English is the source text and the safe fallback. Add every new screen-copy entry to both Hindi and Hinglish in the same change. The catalog check verifies parity, duplicate keys, and blank translations. Do not add runtime translation services, automatic translation requests, or repeated per-render catalog generation.

Prefer the typed `translateUi(locale, "namespace.key")` catalog for reusable product terms, workflow states, and messages shared across multiple screens. Use `formatUiCurrency`, `formatUiNumber`, and `formatUiDate` for locale-aware values rather than hardcoding `en-IN` formatting.

Every standalone entry point that renders a component using `useLocale` must be wrapped in `LanguageProvider`. The normal portal, native desktop, offline Photo Studio, and isolated Photo Studio browser-test entry points all provide the context.
