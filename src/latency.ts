/** 请求耗时采样(统计卡"平均延迟"用),与 .NET LatencyTracker 语义一致 */
export class LatencyTracker {
  private readonly samples = new Float64Array(64);
  private count = 0;

  add(microseconds: number): void {
    const i = (this.count++) % 64;
    this.samples[i] = microseconds;
  }

  get avgMs(): number {
    const n = Math.min(this.count, 64);
    if (n === 0) return 0;
    let sum = 0;
    for (let i = 0; i < n; i++) sum += this.samples[i];
    return Math.round((sum / n / 1000) * 10) / 10;
  }

  get last12(): number[] {
    const res = new Array<number>(12).fill(0);
    for (let i = 0; i < 12; i++) {
      const idx = this.count - 12 + i;
      if (idx >= 0 && idx < this.count) {
        res[i] = Math.round((this.samples[idx % 64] / 1000) * 10) / 10;
      }
    }
    return res;
  }
}
