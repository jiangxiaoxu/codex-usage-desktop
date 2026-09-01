# Token and cost model

## Token accounting units

One stored usage event comes from one accepted `token_count` record. The parser uses `last_token_usage`, not the cumulative total, so the event fields are incremental usage for the record.

| Metric | Definition |
| --- | --- |
| Input | `input_tokens` |
| Cached input | `cached_input_tokens`, a subset of input |
| Uncached input | `input_tokens - cached_input_tokens` |
| Output | `output_tokens` |
| Reasoning output | `reasoning_output_tokens`, a subset of output |
| Other output | `output_tokens - reasoning_output_tokens` |
| Canonical total tokens | `input_tokens + output_tokens` |

`reasoning_output_tokens` is not an additional output bucket. It partitions `output_tokens`; the dashboard never adds it a second time to canonical total tokens or total output cost. Likewise, cached input is part of input, not extra input.

The parser rejects events where cached input exceeds input or reasoning output exceeds output. It also skips only adjacent complete cumulative snapshots and zero-breakdown snapshots. Consequently, totals are estimates from the usable local rollout records, not a billing invoice.

## Model categories

The supported priced families are `gpt-5.6`, `gpt-5.5` and `gpt-5.4`. Exact configured models are priced as follows, in USD per 1M tokens.

| Source model | Uncached input | Cached input | Output |
| --- | ---: | ---: | ---: |
| `gpt-5.6` | 5 | 0.5 | 30 |
| `gpt-5.6-sol` | 5 | 0.5 | 30 |
| `gpt-5.6-terra` | 2 | 0.2 | 12 |
| `gpt-5.6-luna` | 0.2 | 0.02 | 1.2 |
| `gpt-5.5` | 5 | 0.5 | 30 |
| `gpt-5.4` | 2.5 | 0.25 | 15 |
| `gpt-5.4-mini` | 0.75 | 0.075 | 4.5 |
| `gpt-5.4-nano` | 0.2 | 0.02 | 1.25 |

The `gpt-5.6` alias is priced as GPT-5.6 Sol. The exact source model `codex-auto-review` is preserved as its own category. Because no rate is configured, its tokens remain visible, are included in `unpricedTokens`, and have no estimated cost. Models outside the supported families are grouped as `Others`. `Others` remains visible in token statistics but has a zero token-cost estimate. The exact source model value `unknown` is grouped as `Unknown attribution`; its tokens are included in `unpricedTokens` and are not represented as a zero-cost priced model. A newly observed source model within a supported family but missing from the exact rate table is also unpriced.

The configured GPT-5.6 prices are the standard API prices in OpenAI's 2026-07-30 price-performance announcement. They are intentionally fixed to that rate card and do not follow later promotions or price changes.

## Cost calculation

For a priced event, the base-rate calculation is:

```text
baselineUncachedInput = (input - cachedInput) * inputRate / 1,000,000
baselineCachedInput   = cachedInput * cachedInputRate / 1,000,000
baselineReasoning     = reasoningOutput * outputRate / 1,000,000
baselineOtherOutput   = (output - reasoningOutput) * outputRate / 1,000,000
baselineTotal         = sum of the four baseline components
```

When `input_tokens > 272_000`, long-context rates apply to the full event: uncached and cached input components are each `2x`, and reasoning and other output components are each `1.5x`. At `272_000` or below, every component stays at its base rate. `actualTotal` is the sum of the adjusted four components, `longContextPremium` is `actualTotal - baselineTotal`, and `actualToBaselineMultiplier` is `actualTotal / baselineTotal` when the baseline is positive. The dashboard shows no multiplier for a zero baseline.

The UI shows the four actual-cost components separately. The dashboard summary shows baseline cost, actual total cost, and the actual-to-baseline multiplier; long-context premium remains an internal aggregate because it overlaps the adjusted input and output costs. The model and role tables show the same multiplier per row alongside that row's share of actual total cost. Reasoning and other output have the same configured output rate; separating them is analytical only and does not change total output pricing. The current rate table is applied uniformly to all stored usage, without preserving historical rate versions. All displayed USD values are standard API-equivalent estimates before discounts, not Plus/Pro subscription charges or a provider invoice.

## Codex subscription context policy

This application treats every observed rollout as Codex activity. The long-context rule above applies only to `gpt-5.6`, `gpt-5.6-sol`, `gpt-5.6-terra`, `gpt-5.6-luna`, `gpt-5.5`, and `gpt-5.4`. `gpt-5.4-mini` and `gpt-5.4-nano` always use their base rates. The Codex rate card has no separate cache-write surcharge, so observed `cache_write_input_tokens` are not stored or added as another cost component. Tool-call charges, subscription charges, taxes, discounts and credits are also excluded. These API-equivalent estimates are not Plus/Pro subscription charges or a provider invoice.

## Time, filters and percentages

The WinUI view model converts Singapore local control values to UTC and queries the half-open range `[startUtc, endUtc)`. Model and subject facets are calculated over all events in that time range before the current model/subject selection, so one filter does not make the other filter's choices disappear. The main-thread filter accepts a complete session ID, uses an exact main `ConversationId` as its root, and includes every descendant-agent event.

Displayed USD values are formatted to one decimal place. A displayed price share is `group.cost.total / selected.summary.cost.total`; it is a cost share, not a token share. When the selected total cost is zero, a meaningful positive price share is not available.
