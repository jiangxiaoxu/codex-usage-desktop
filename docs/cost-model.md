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
| `gpt-5.6-terra` | 2.5 | 0.25 | 15 |
| `gpt-5.6-luna` | 1 | 0.1 | 6 |
| `gpt-5.5` | 5 | 0.5 | 30 |
| `gpt-5.4` | 2.5 | 0.25 | 15 |
| `gpt-5.4-mini` | 0.75 | 0.075 | 4.5 |
| `gpt-5.4-nano` | 0.2 | 0.02 | 1.25 |

The `gpt-5.6` alias is priced as GPT-5.6 Sol. Models outside the supported families are grouped as `Others`. `Others` remains visible in token statistics but has a zero token-cost estimate. The exact source model value `unknown` is grouped as `Unknown attribution`; its tokens are included in `unpricedTokens` and are not represented as a zero-cost priced model. A newly observed source model within a supported family but missing from the exact rate table is also unpriced.

## Cost calculation

For a priced event, the calculation is:

```text
uncachedInputCost = (input - cachedInput) * inputRate / 1,000,000
cachedInputCost   = cachedInput * cachedInputRate / 1,000,000
reasoningCost     = reasoningOutput * outputRate / 1,000,000
otherOutputCost   = (output - reasoningOutput) * outputRate / 1,000,000
totalCost         = sum of the four components
```

The UI shows the four cost components separately. Reasoning and other output have the same configured output rate; separating them is analytical only and does not change total output pricing.

## Codex subscription context policy

This application treats every observed rollout as Codex subscription usage. Input length never applies an additional long-context multiplier to any model; all token-cost estimates use the base rates above. This is an application reporting convention, not a claim about a current provider invoice. In particular, cache-write charges, tool-call charges, subscription charges, taxes, discounts and credits are absent from rollout token records and are excluded.

## Time, filters and percentages

The renderer converts Singapore local control values to UTC and queries the half-open range `[startUtc, endUtc)`. Model and subject facets are calculated over all events in that time range before the current model/subject selection, so one filter does not make the other filter's choices disappear. Agent path search is applied after model and subject matching.

Displayed USD values are formatted to one decimal place. A displayed price share is `group.cost.total / selected.summary.cost.total`; it is a cost share, not a token share. When the selected total cost is zero, a meaningful positive price share is not available. CSV exports retain a higher-precision decimal `total_cost_usd` value. Their fields are `timestamp_sgt`, `conversation_id`, `rollout_id`, `thread_type`, `agent_role`, `agent_path`, `model_category`, `source_model`, `input_tokens`, `cached_input_tokens`, `output_tokens`, `reasoning_output_tokens`, `other_output_tokens` and `total_cost_usd`.
