import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DhanApiService } from '../../core/dhan-api.service';
import { MarketScannerInstrument } from '../../core/models';

type ScannerSide = 'LONG' | 'SHORT';
type FilterKey = 'marketTrend' | 'relativeStrength' | 'vwapEma' | 'volume' | 'entrySetup' | 'riskReward' | 'rsi';

interface ScannerFilter {
  key: FilterKey;
  label: string;
  description: string;
  enabled: boolean;
  weight: number;
}

interface RankedInstrument {
  instrument: MarketScannerInstrument;
  score: number;
  reasons: string[];
  entry: number;
  stopLoss: number;
  target: number;
  riskReward: number;
}

const createFilters = (): ScannerFilter[] => [
  { key: 'marketTrend', label: 'Market trend', description: 'NIFTY 50 trend alignment', enabled: true, weight: 15 },
  { key: 'relativeStrength', label: 'Relative strength / weakness', description: 'Versus NIFTY 50 over recent sessions', enabled: true, weight: 10 },
  { key: 'vwapEma', label: 'VWAP / 20-50 EMA alignment', description: 'Directional price, VWAP and EMA alignment', enabled: true, weight: 25 },
  { key: 'volume', label: 'Volume confirmation', description: 'Session volume versus its recent average', enabled: true, weight: 20 },
  { key: 'entrySetup', label: 'Breakout / pullback setup', description: 'Breakout or pullback confirmation', enabled: true, weight: 20 },
  { key: 'riskReward', label: 'Risk / reward', description: 'Minimum 1:2 risk/reward (always required)', enabled: true, weight: 10 },
  { key: 'rsi', label: 'RSI support', description: 'Soft tie-break only; never an entry gate', enabled: true, weight: 0 }
];

@Component({
  selector: 'app-scanner',
  imports: [DatePipe, DecimalPipe, FormsModule],
  templateUrl: './scanner.component.html'
})
export class ScannerComponent implements OnInit {
  longFilters = createFilters();
  shortFilters = createFilters();
  instruments: MarketScannerInstrument[] = [];
  generatedAt = '';
  warning = '';
  errorMessage = '';
  loading = false;

  constructor(private readonly api: DhanApiService) {}

  ngOnInit(): void {
    void this.refresh();
  }

  get longRows(): RankedInstrument[] {
    return this.rank('LONG', this.longFilters);
  }

  get shortRows(): RankedInstrument[] {
    return this.rank('SHORT', this.shortFilters);
  }

  async refresh(): Promise<void> {
    if (this.loading) {
      return;
    }

    this.loading = true;
    this.errorMessage = '';
    this.warning = '';
    try {
      const result = await this.api.getStockFuturesScan();
      this.instruments = result.instruments || [];
      this.generatedAt = result.generatedAt;
      this.warning = result.warning || '';
    } catch (err) {
      this.instruments = [];
      this.generatedAt = '';
      this.errorMessage = `Could not load scanner data: ${(err as Error).message}`;
    } finally {
      this.loading = false;
    }
  }

  trackInstrument(_: number, row: RankedInstrument): string {
    return row.instrument.securityId;
  }

  hasScoringFilters(filters: ScannerFilter[]): boolean {
    return filters.some(filter => filter.enabled && filter.weight > 0);
  }

  private rank(side: ScannerSide, filters: ScannerFilter[]): RankedInstrument[] {
    const selected = filters.filter(filter => filter.enabled);
    const weightedFilters = selected.filter(filter => filter.weight > 0);
    const totalWeight = weightedFilters.reduce((total, filter) => total + filter.weight, 0);
    if (totalWeight === 0) {
      return [];
    }

    return this.instruments
      .map(instrument => {
        const entry = side === 'LONG' ? instrument.longEntryTrigger : instrument.shortEntryTrigger;
        const stopLoss = side === 'LONG' ? instrument.longStopLoss : instrument.shortStopLoss;
        const target = side === 'LONG' ? instrument.longTarget : instrument.shortTarget;
        const riskReward = side === 'LONG' ? instrument.longRiskReward : instrument.shortRiskReward;
        const reasons = this.reasons(instrument, side, selected);
        const earnedWeight = weightedFilters.reduce(
          (earned, filter) => earned + filter.weight * this.contribution(instrument, side, filter.key),
          0
        );

        return {
          instrument,
          score: earnedWeight / totalWeight * 100,
          reasons,
          entry,
          stopLoss,
          target,
          riskReward
        };
      })
      .filter(row => this.isEligible(row, side, selected))
      .sort((a, b) =>
        b.score - a.score ||
        this.rsiTieBreak(b.instrument, side, selected) - this.rsiTieBreak(a.instrument, side, selected) ||
        a.instrument.underlyingSymbol.localeCompare(b.instrument.underlyingSymbol)
      )
      .slice(0, 5);
  }

  private isEligible(row: RankedInstrument, side: ScannerSide, selected: ScannerFilter[]): boolean {
    const { instrument, entry, stopLoss, target, riskReward } = row;
    const validRisk = side === 'LONG'
      ? entry > stopLoss && stopLoss > 0 && target > entry
      : stopLoss > entry && target > 0 && target < entry;

    if (!instrument.isLiquid || !validRisk || riskReward < 2) {
      return false;
    }

    return selected
      .filter(filter => filter.weight > 0)
      .every(filter => this.matches(instrument, side, filter.key));
  }

  private matches(instrument: MarketScannerInstrument, side: ScannerSide, key: FilterKey): boolean {
    switch (key) {
      case 'marketTrend':
        return side === 'LONG' ? instrument.longMarketTrend : instrument.shortMarketTrend;
      case 'relativeStrength':
        return side === 'LONG' ? instrument.longRelativeStrength : instrument.shortRelativeStrength;
      case 'vwapEma':
        return side === 'LONG'
          ? instrument.longAboveVwap && instrument.longEmaAlignment
          : instrument.shortBelowVwap && instrument.shortEmaAlignment;
      case 'volume':
        return instrument.volumeConfirmation;
      case 'entrySetup':
        return side === 'LONG'
          ? instrument.longBreakout || instrument.longPullbackConfirmation
          : instrument.shortBreakdown || instrument.shortPullbackConfirmation;
      case 'riskReward':
        return (side === 'LONG' ? instrument.longRiskReward : instrument.shortRiskReward) >= 2;
      case 'rsi':
        return side === 'LONG' ? instrument.longRsiConfirmation : instrument.shortRsiConfirmation;
    }
  }

  private contribution(instrument: MarketScannerInstrument, side: ScannerSide, key: FilterKey): number {
    if (key === 'riskReward') {
      const ratio = side === 'LONG' ? instrument.longRiskReward : instrument.shortRiskReward;
      return Math.min(ratio / 3, 1);
    }
    return this.matches(instrument, side, key) ? 1 : 0;
  }

  private reasons(instrument: MarketScannerInstrument, side: ScannerSide, selected: ScannerFilter[]): string[] {
    const reasons: string[] = [];
    const enabled = (key: FilterKey): boolean => selected.some(filter => filter.key === key);
    if (enabled('marketTrend') && this.matches(instrument, side, 'marketTrend')) {
      reasons.push(`NIFTY 50 trend supports ${side.toLowerCase()} setups`);
    }
    if (enabled('relativeStrength') && this.matches(instrument, side, 'relativeStrength') && instrument.relativeStrengthPercent !== null) {
      reasons.push(`${side === 'LONG' ? 'Outperforming' : 'Underperforming'} NIFTY 50 by ${Math.abs(instrument.relativeStrengthPercent).toFixed(2)}%`);
    }
    if (enabled('vwapEma') && this.matches(instrument, side, 'vwapEma')) {
      reasons.push(`VWAP and 20/50 EMA alignment confirmed`);
    }
    if (enabled('volume') && instrument.volumeConfirmation) {
      reasons.push('Volume confirmed versus recent average');
    }
    if (enabled('entrySetup')) {
      if ((side === 'LONG' && instrument.longBreakout) || (side === 'SHORT' && instrument.shortBreakdown)) {
        reasons.push(side === 'LONG' ? 'Breakout above prior-session high' : 'Breakdown below prior-session low');
      } else if ((side === 'LONG' && instrument.longPullbackConfirmation) || (side === 'SHORT' && instrument.shortPullbackConfirmation)) {
        reasons.push('Pullback confirmation near 20 EMA');
      }
    }
    if (enabled('rsi') && this.matches(instrument, side, 'rsi')) {
      reasons.push(`RSI supports momentum (${instrument.rsi14.toFixed(1)})`);
    }
    reasons.push(`Liquidity confirmed: ₹${(instrument.averageDailyTurnover / 10_000_000).toFixed(1)} Cr average daily turnover`);
    return reasons;
  }

  private rsiTieBreak(instrument: MarketScannerInstrument, side: ScannerSide, selected: ScannerFilter[]): number {
    return selected.some(filter => filter.key === 'rsi' && filter.enabled) &&
      this.matches(instrument, side, 'rsi') ? 1 : 0;
  }
}
