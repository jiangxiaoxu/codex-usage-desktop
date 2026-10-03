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

The supported priced families are `gpt-6-astra`, `gpt-6.1-sol`, `gpt-6-sol`, `gpt-6-luna`, `gpt-5.6`, `gpt-5.5` and `gpt-5.4`. Exact configured models are priced as follows, in USD per 1M tokens.

| Source model | Uncached input | Cached input | Output |
| --- | ---: | ---: | ---: |
| `gpt-6-astra` | 10 | 1 | 50 |
| `gpt-6.1-sol` | 2 | 0.1 | 10 |
| `gpt-6-sol` | 2 | 0.2 | 10 |
| `gpt-6-luna` | 0.1 | 0.01 | 0.5 |
| `gpt-5.6` | 5 | 0.5 | 30 |
| `gpt-5.6-sol` | 5 | 0.5 | 30 |
| `gpt-5.6-terra` | 2 | 0.2 | 12 |
| `gpt-5.6-luna` | 0.2 | 0.02 | 1.2 |
| `gpt-5.5` | 5 | 0.5 | 30 |
| `gpt-5.4` | 2.5 | 0.25 | 15 |
| `gpt-5.4-mini` | 0.75 | 0.075 | 4.5 |
| `gpt-5.4-nano` | 0.2 | 0.02 | 1.25 |

The `gpt-5.6` alias is priced as GPT-5.6 Sol. The exact source model `codex-auto-review` is preserved as its own category. Because no rate is configured, its tokens remain visible, are included in `unpricedTokens`, and have no estimated cost. Models outside the supported families are grouped as `Others`. `Others` remains visible in token statistics but has a zero token-cost estimate. The exact source model value `unknown` is grouped as `Unknown attribution`; its tokens are included in `unpricedTokens` and are not represented as a zero-cost priced model. A newly observed source model within a supported family but missing from the exact rate table is also unpriced.

The configured GPT-6 Astra, Sol and Luna prices are fixed Standard API rates of `$10/$1/$50`, `$2/$0.2/$10` and `$0.1/$0.01/$0.5` per 1M uncached input, cached input and output tokens, respectively. GPT-6.1 Sol uses fixed Standard API rates of `$2/$0.1/$10`, verified against the [official model page](https://developers.openai.com/api/docs/models/gpt-6.1-sol) on 2026-09-30. The GPT-5.6 prices remain the standard API prices in OpenAI's 2026-07-30 price-performance announcement. All configured rates are intentionally fixed and do not follow later promotions or price changes.

## Cost calculation

For a priced event, the base-rate calculation is:

```text
baselineUncachedInput = (input - cachedInput) * inputRate / 1,000,000
baselineCachedInput   = cachedInput * cachedInputRate / 1,000,000
baselineReasoning     = reasoningOutput * outputRate / 1,000,000
baselineOtherOutput   = (output - reasoningOutput) * outputRate / 1,000,000
baselineTotal         = sum of the four baseline components
```

When `input_tokens > 272_000`, long-context rates apply to the full event: uncached and cached input components are each `2x`, and reasoning and other output components are each `1.5x`. At `272_000` or below, these context multipliers remain `1x`. Fast events then multiply all four adjusted components by `2.5x`. This fixed Fast multiplier is the application's agreed estimation policy. It is not a claim about the provider's API invoice or subscription allowance usage.

`longContextPremium` is the context-adjusted total minus `baselineTotal`, before applying Fast. `fastModePremium` is the final total minus the context-adjusted total. Thus `actualTotal = baselineTotal + longContextPremium + fastModePremium`. The UI's four actual-cost components already contain both adjustments; the premiums are overlapping analytical aggregates and must not be added as extra composition buckets. `actualToBaselineMultiplier` is `actualTotal / baselineTotal` when the baseline is positive. Across mixed events it is a cost-weighted multiplier, not an average of event multipliers. The dashboard shows no multiplier for a zero baseline.

The dashboard summary, model table and role table separately show `长上下文费用倍率` and `Fast 费用倍率` as multipliers. Let `contextAdjustedTotal = baselineTotal + longContextPremium`. The long-context multiplier is `contextAdjustedTotal / baselineTotal`; the Fast multiplier is `actualTotal / contextAdjustedTotal`. Each is absent when its denominator is zero. Their product corresponds to the combined actual-to-baseline multiplier, and each displayed value reflects the cost-weighted effect over the selected events. With only Fast events the Fast multiplier is `2.5x`, regardless of long-context usage; with no Fast surcharge it is `1x` when the denominator is positive. Model and role rows also show their share of actual total cost. Reasoning and other output have the same configured output rate; separating them is analytical only and does not change total output pricing. The current rate table and Fast multiplier apply uniformly to all stored usage, without preserving historical rate versions. All displayed USD values are estimates before discounts, not Plus/Pro subscription charges or a provider invoice.

## Observed service tier

The parser reads `event_msg.thread_settings_applied.thread_settings.service_tier` for the snapshot's owning thread. `priority` and `fast` are classified as Fast; `default` and `standard` as Standard. Missing, null, malformed or unrecognized values are Unknown and clear any previous explicit Fast setting for future turns. For attribution, each turn retains the observed setting captured at its start; later snapshots apply to subsequent turns. This matches the main thread's turn configuration behavior. Subagents can inherit a changed root setting between steps without a new child snapshot, so their actual tier cannot always be recovered. Copied ancestor settings do not establish the child thread's mode.

These snapshots record selected settings, not the tier confirmed by each response. Rollout token records do not contain the actual response tier. Unknown events retain the existing Standard and long-context estimate without a Fast surcharge. Raw token counts are never multiplied. The ledger and summary retain Fast calls, canonical Fast tokens, unknown-mode coverage and Fast premium; the UI shows the long-context and Fast expense multipliers separately. Parser revision changes reparse readable historical sources and replace their stored events, without duplicating usage. Missing historical settings cannot be recovered from the current Codex configuration.

## Codex subscription context policy

This application treats every observed rollout as Codex activity. The long-context rule above applies only to `gpt-6-astra`, `gpt-6.1-sol`, `gpt-6-sol`, `gpt-6-luna`, `gpt-5.6`, `gpt-5.6-sol`, `gpt-5.6-terra`, `gpt-5.6-luna`, `gpt-5.5`, and `gpt-5.4`. `gpt-5.4-mini` and `gpt-5.4-nano` have no long-context surcharge. Fast applies only to events with a configured model rate and an observed Fast setting; unpriced models remain unpriced. The Codex rate card has no separate cache-write surcharge, so observed `cache_write_input_tokens` are not stored or added as another cost component. Tool-call charges, subscription charges, taxes, discounts and credits are also excluded. These estimates are not Plus/Pro subscription charges or a provider invoice.

## Time, filters and percentages

The WinUI view model converts Singapore local control values to UTC and queries the half-open range `[startUtc, endUtc)`. Model and subject facets are calculated over all events in that time range before the current model/subject selection, so one filter does not make the other filter's choices disappear. The main-thread filter accepts a complete session ID, uses an exact main `ConversationId` as its root, and includes every descendant-agent event.

Displayed USD values are formatted to one decimal place. A displayed price share is `group.cost.total / selected.summary.cost.total`; it is a cost share, not a token share. When the selected total cost is zero, a meaningful positive price share is not available.
