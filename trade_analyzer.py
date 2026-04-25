#!/usr/bin/env python3
"""
Trade Setup Analyzer — one tool, ultimate.

Feed it a setup JSON, get back a structured risk-first assessment.

Usage:
    export ANTHROPIC_API_KEY=sk-ant-...
    python trade_analyzer.py setup.json
    python trade_analyzer.py -            # read from stdin
    python trade_analyzer.py --sample     # print a sample setup schema

Requires:
    pip install anthropic>=0.40
"""

from __future__ import annotations

import argparse
import json
import math
import sys
from pathlib import Path
from typing import Any

import anthropic


SYSTEM_PROMPT = """You are a senior discretionary trader and risk manager with 20 years of experience across equities, futures, and crypto. You specialize in multi-timeframe technical analysis, market microstructure, and strict risk management. You are NOT a price predictor — you are a decision support analyst who evaluates setups against defined rules and flags risks.

## YOUR ROLE
Analyze the trade setup provided against the user's playbook and current market context. Return a structured, honest assessment. If the setup is weak, say so directly. If there are red flags, lead with them. Your job is to protect the trader from bad trades, not to validate their bias.

## HARD RULES — NEVER VIOLATE
1. NEVER claim to predict price direction. Use language like "setup suggests", "structure favors", "if X holds, then Y is likely".
2. NEVER recommend a trade that violates the user's playbook, even if it "looks good".
3. NEVER recommend sizing above max_risk_pct. Always calculate actual shares/contracts based on stop distance.
4. If daily_pnl < -3%, recommended_action MUST be "blocked_by_risk_rules".
5. If consecutive_losses >= 3, recommended_action MUST include "size_down".
6. If a major catalyst (FOMC, earnings) is within the holding period, flag it prominently.
7. If trade is counter-trend on the higher timeframe, flag it and require exceptional confluence.
8. If volume is below average on a breakout setup, mark as "marginal" or "invalid".
9. NEVER invent indicator values, news, or context not provided in the input.
10. If data is missing or contradictory, say so in red_flags. Do not guess.

## TONE
Direct, professional, risk-first. You are the friend who tells the trader "no, that's a bad trade" when it is. You do not cheerlead. You do not hedge everything with "but it could also go the other way" on every sentence — give a clear read, then list the risks.

Return ONLY the JSON object matching the provided schema. No prose before or after."""


OUTPUT_SCHEMA: dict[str, Any] = {
    "type": "object",
    "properties": {
        "setup_validity": {"type": "string", "enum": ["valid", "invalid", "marginal"]},
        "playbook_match": {
            "type": "object",
            "properties": {
                "setup_name": {"type": "string"},
                "rules_met": {"type": "array", "items": {"type": "string"}},
                "rules_violated": {"type": "array", "items": {"type": "string"}},
            },
            "required": ["setup_name", "rules_met", "rules_violated"],
            "additionalProperties": False,
        },
        "confluence_score": {"type": "number", "minimum": 0, "maximum": 10},
        "confluence_factors": {
            "type": "object",
            "properties": {
                "technical": {"type": "array", "items": {"type": "string"}},
                "volume": {"type": "string"},
                "multi_timeframe": {"type": "string"},
                "market_context": {"type": "string"},
                "news_catalyst": {"type": "string"},
            },
            "required": ["technical", "volume", "multi_timeframe", "market_context", "news_catalyst"],
            "additionalProperties": False,
        },
        "red_flags": {"type": "array", "items": {"type": "string"}},
        "trade_plan": {
            "type": "object",
            "properties": {
                "bias": {"type": "string", "enum": ["long", "short", "no_trade"]},
                "entry_zone": {"type": "string"},
                "invalidation": {"type": "string"},
                "stop_loss": {"type": "string"},
                "targets": {
                    "type": "array",
                    "items": {
                        "type": "object",
                        "properties": {
                            "level": {"type": "string"},
                            "rationale": {"type": "string"},
                            "r_multiple": {"type": "number"},
                        },
                        "required": ["level", "rationale", "r_multiple"],
                        "additionalProperties": False,
                    },
                },
                "position_size_shares": {"type": "string"},
                "dollar_risk": {"type": "string"},
                "time_stop": {"type": "string"},
            },
            "required": [
                "bias", "entry_zone", "invalidation", "stop_loss",
                "targets", "position_size_shares", "dollar_risk", "time_stop",
            ],
            "additionalProperties": False,
        },
        "risk_checks": {
            "type": "object",
            "properties": {
                "within_daily_loss_limit": {"type": "boolean"},
                "correlation_safe": {"type": "boolean"},
                "size_reduction_required": {"type": "boolean"},
                "reason_if_blocked": {"type": ["string", "null"]},
            },
            "required": ["within_daily_loss_limit", "correlation_safe", "size_reduction_required", "reason_if_blocked"],
            "additionalProperties": False,
        },
        "historical_context": {"type": "string"},
        "honest_summary": {"type": "string"},
        "confidence_in_analysis": {"type": "string", "enum": ["low", "medium", "high"]},
        "recommended_action": {
            "type": "string",
            "enum": [
                "take_trade", "wait_for_better_entry", "skip",
                "paper_trade_only", "size_down", "blocked_by_risk_rules",
            ],
        },
    },
    "required": [
        "setup_validity", "playbook_match", "confluence_score", "confluence_factors",
        "red_flags", "trade_plan", "risk_checks", "historical_context",
        "honest_summary", "confidence_in_analysis", "recommended_action",
    ],
    "additionalProperties": False,
}


SAMPLE_SETUP: dict[str, Any] = {
    "user_profile": {
        "trader_name": "jdoe",
        "account_size": 50000,
        "max_risk_pct": 1.0,
        "timeframe_style": "swing",
        "level": "intermediate",
        "daily_pnl_pct": -0.8,
        "open_positions": [{"symbol": "MSFT", "side": "long", "size_pct": 2.5}],
        "consecutive_losses": 1,
    },
    "playbook_rules": [
        {
            "name": "pullback_to_20ma_in_uptrend",
            "conditions": [
                "daily trend up (higher highs/lows)",
                "price within 1 ATR of 20MA",
                "RSI(14) between 40-60",
                "volume on reversal >= 20d average",
            ],
            "invalidation": "close below 50MA",
        },
        {
            "name": "breakout_from_base",
            "conditions": [
                "4+ week base with declining volume",
                "breakout close > resistance on >1.5x avg volume",
                "weekly trend not down",
            ],
        },
    ],
    "setup": {
        "symbol": "NVDA",
        "asset_class": "equity",
        "price": 875.40,
        "timeframe": "daily",
        "setup_name": "pullback_to_20ma_in_uptrend",
        "technicals": {
            "rsi_14": 48,
            "atr_14": 22.50,
            "atr_percentile_1yr": 65,
            "volume_zscore_20d": 0.3,
            "ma_distances": {"20": -0.5, "50": 3.2, "200": 18.4},
            "support_resistance": {"support": [860, 842], "resistance": [895, 920]},
            "trend_structure": "higher_highs_lows",
        },
        "multi_timeframe": {
            "daily_trend": "up",
            "weekly_trend": "up",
            "monthly_bias": "up",
        },
    },
    "market_context": {
        "spy_today_pct": 0.4,
        "qqq_today_pct": 0.7,
        "vix": 14.2,
        "vix_regime": "low_vol",
        "dxy_change_pct": -0.1,
        "ten_year_yield": 4.25,
        "sector": {"name": "semis", "today_pct": 1.2},
        "correlation_to_spy": 0.78,
        "market_regime": "trending",
    },
    "news_last_24h": [
        {"headline": "Analyst upgrade at major bank, PT raised to $1000", "sentiment": "positive"}
    ],
    "upcoming_catalysts_72h": [
        {"event": "earnings", "when": "in 9 days", "relevant": True}
    ],
}


def calc_position_size(account_size: float, risk_pct: float, entry: float, stop: float) -> dict[str, Any]:
    """Pre-compute sizing so the model doesn't have to do arithmetic."""
    if entry <= 0 or stop <= 0 or entry == stop:
        return {"shares": 0, "dollar_risk": 0, "stop_distance": 0, "note": "invalid entry/stop"}
    stop_distance = abs(entry - stop)
    dollar_risk = account_size * (risk_pct / 100.0)
    shares = math.floor(dollar_risk / stop_distance)
    return {
        "shares": shares,
        "dollar_risk": round(shares * stop_distance, 2),
        "max_dollar_risk_allowed": round(dollar_risk, 2),
        "stop_distance": round(stop_distance, 4),
    }


def precheck_hard_rules(setup: dict[str, Any]) -> dict[str, Any]:
    """Compute deterministic risk flags before calling the model."""
    profile = setup.get("user_profile", {})
    daily_pnl_pct = profile.get("daily_pnl_pct", 0.0)
    consecutive_losses = profile.get("consecutive_losses", 0)

    checks = {
        "blocked_by_daily_loss": daily_pnl_pct <= -3.0,
        "size_down_required": consecutive_losses >= 3,
        "daily_pnl_pct": daily_pnl_pct,
        "consecutive_losses": consecutive_losses,
    }
    return checks


def build_user_prompt(setup: dict[str, Any]) -> str:
    prechecks = precheck_hard_rules(setup)

    sizing_hint = None
    s = setup.get("setup", {})
    profile = setup.get("user_profile", {})
    entry = s.get("price")
    invalidation = s.get("invalidation_price") or s.get("technicals", {}).get("support_resistance", {}).get("support", [None])[0]
    if entry and invalidation and profile.get("account_size") and profile.get("max_risk_pct"):
        sizing_hint = calc_position_size(
            float(profile["account_size"]),
            float(profile["max_risk_pct"]),
            float(entry),
            float(invalidation),
        )

    return (
        "## DETERMINISTIC PRE-CHECKS (computed before analysis)\n"
        f"{json.dumps(prechecks, indent=2)}\n\n"
        "## SIZING HINT (computed from account/risk/stop — use this in trade_plan)\n"
        f"{json.dumps(sizing_hint, indent=2) if sizing_hint else 'insufficient data to precompute'}\n\n"
        "## SETUP INPUT\n"
        f"{json.dumps(setup, indent=2, default=str)}\n\n"
        "Now analyze this setup per your role. Return ONLY the JSON object matching the required schema. "
        "If blocked_by_daily_loss is true, recommended_action must be 'blocked_by_risk_rules'. "
        "If size_down_required is true, recommended_action must be 'size_down' (or 'blocked_by_risk_rules' if also blocked)."
    )


def analyze(setup: dict[str, Any], model: str = "claude-opus-4-7") -> dict[str, Any]:
    client = anthropic.Anthropic()

    response = client.messages.create(
        model=model,
        max_tokens=16000,
        thinking={"type": "adaptive"},
        output_config={
            "effort": "high",
            "format": {"type": "json_schema", "schema": OUTPUT_SCHEMA},
        },
        system=[
            {
                "type": "text",
                "text": SYSTEM_PROMPT,
                "cache_control": {"type": "ephemeral"},
            }
        ],
        messages=[{"role": "user", "content": build_user_prompt(setup)}],
    )

    text = next((b.text for b in response.content if b.type == "text"), "")
    try:
        return json.loads(text)
    except json.JSONDecodeError as e:
        raise RuntimeError(f"Model did not return valid JSON: {e}\nRaw output:\n{text}") from e


def main() -> int:
    parser = argparse.ArgumentParser(description="Trade setup analyzer — risk-first decision support.")
    parser.add_argument("input", nargs="?", help="Path to setup JSON, or '-' for stdin")
    parser.add_argument("--sample", action="store_true", help="Print a sample setup JSON and exit")
    parser.add_argument("--model", default="claude-opus-4-7", help="Claude model ID")
    args = parser.parse_args()

    if args.sample:
        json.dump(SAMPLE_SETUP, sys.stdout, indent=2)
        sys.stdout.write("\n")
        return 0

    if not args.input:
        parser.print_help()
        return 2

    if args.input == "-":
        setup = json.load(sys.stdin)
    else:
        setup = json.loads(Path(args.input).read_text())

    result = analyze(setup, model=args.model)
    json.dump(result, sys.stdout, indent=2)
    sys.stdout.write("\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
