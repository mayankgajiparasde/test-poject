| Scenario | Value | Workbook | Engine | Difference | Result | Explanation |
|---|---|---:|---:|---:|---|---|
| Base | Loan commitment | 44,791,500.00 | 44,791,500.00 | 0.00 | PASS | Within tolerance. |
| Base | Initial funded loan (after PIP holdback) | 39,751,500.00 | 39,751,500.00 | 0.00 | PASS | Within tolerance. |
| Base | Equity required at close | 24,966,415.00 | 24,966,415.00 | 0.00 | PASS | Within tolerance. |
| Base | 2028 rooms revenue | 16,298,777.88 | 16,298,777.88 | 0.00 | PASS | Within tolerance. |
| Base | 2028 total revenue | 22,620,062.11 | 22,620,062.11 | 0.00 | PASS | Within tolerance. |
| Base | 2029 total departmental expenses | 8,134,491.18 | 8,134,491.18 | 0.00 | PASS | Within tolerance. |
| Base | 2029 total undistributed expenses | 6,657,219.58 | 6,657,219.58 | 0.00 | PASS | Within tolerance. |
| Base | 2029 NOI | 6,147,003.94 | 6,147,003.94 | 0.00 | PASS | Within tolerance. |
| Base | Month 1 interest | 236,852.69 | 236,852.69 | -0.00 | PASS | Within tolerance. |
| Base | First amortizing payment | 305,344.00 | 305,344.00 | 0.00 | PASS | Within tolerance. |
| Base | Peak loan balance | 44,791,500.00 | 44,791,500.00 | 0.00 | PASS | Within tolerance. |
| Base | Forward 12-month NOI at exit | 6,990,168.13 | 6,990,168.13 | -0.00 | PASS | Within tolerance. |
| Base | Gross sale price | 87,377,101.64 | 87,377,101.64 | 0.00 | PASS | Within tolerance. |
| Base | Loan payoff at exit | 43,446,146.34 | 43,407,596.71 | -38,549.63 | FAIL | Workbook error: reproduced only by switching on #1 Payoff uses the exit month's opening balance (Monthly CF row 73, D73:CU73) |
| Base | Net sale proceeds to equity | 41,309,642.26 | 41,348,191.89 | 38,549.63 | FAIL | Workbook error: reproduced only by switching on #1 Payoff uses the exit month's opening balance (Monthly CF row 73, D73:CU73) |
| Base | Unlevered IRR | 12.3078% | 12.3078% | -0.0000 pp | PASS | Within tolerance. |
| Base | Levered IRR | 18.3677% | 18.3860% | 0.0183 pp | FAIL | Workbook error: reproduced only by switching on #1 Payoff uses the exit month's opening balance (Monthly CF row 73, D73:CU73) |
| Base | Levered equity multiple | 2.1092x | 2.1108x | 0.0015 | FAIL | Workbook error: reproduced only by switching on #1 Payoff uses the exit month's opening balance (Monthly CF row 73, D73:CU73) |
| Base | LP IRR | 16.6365% | 16.6089% | -0.0276 pp | FAIL | Workbook error: reproduced only by switching on #1 Payoff uses the exit month's opening balance (Monthly CF row 73, D73:CU73); #2 Pref accrues at 1 + pref/12 (Monthly CF B37, used by row 87) |
| Base | GP IRR | 30.5001% | 30.7736% | 0.2735 pp | FAIL | Workbook error: reproduced only by switching on #1 Payoff uses the exit month's opening balance (Monthly CF row 73, D73:CU73); #2 Pref accrues at 1 + pref/12 (Monthly CF B37, used by row 87) |
| Base | GP multiple | 3.3820x | 3.4171x | 0.0351 | FAIL | Workbook error: reproduced only by switching on #1 Payoff uses the exit month's opening balance (Monthly CF row 73, D73:CU73); #2 Pref accrues at 1 + pref/12 (Monthly CF B37, used by row 87) |
| Base | Check: loan balance after exit (must be 0) | -38,549.63 | 0.00 | 38,549.63 | FAIL | Workbook error: reproduced only by switching on #1 Payoff uses the exit month's opening balance (Monthly CF row 73, D73:CU73) |
| Upside | Loan commitment | 44,791,500.00 | 44,791,500.00 | 0.00 | PASS | Within tolerance. |
| Upside | Initial funded loan (after PIP holdback) | 39,751,500.00 | 39,751,500.00 | 0.00 | PASS | Within tolerance. |
| Upside | Equity required at close | 24,966,415.00 | 24,966,415.00 | 0.00 | PASS | Within tolerance. |
| Upside | 2028 rooms revenue | 17,288,623.23 | 17,288,623.23 | 0.00 | PASS | Within tolerance. |
| Upside | 2028 total revenue | 23,820,689.88 | 23,820,689.88 | 0.00 | PASS | Within tolerance. |
| Upside | 2029 total departmental expenses | 8,382,774.43 | 8,382,774.43 | -0.00 | PASS | Within tolerance. |
| Upside | 2029 total undistributed expenses | 6,784,566.66 | 6,784,566.66 | 0.00 | PASS | Within tolerance. |
| Upside | 2029 NOI | 7,032,258.46 | 7,032,258.46 | -0.00 | PASS | Within tolerance. |
| Upside | Month 1 interest | 223,602.19 | 223,602.19 | 0.00 | PASS | Within tolerance. |
| Upside | First amortizing payment | 293,280.39 | 293,280.39 | -0.00 | PASS | Within tolerance. |
| Upside | Peak loan balance | 44,791,500.00 | 44,791,500.00 | 0.00 | PASS | Within tolerance. |
| Upside | Forward 12-month NOI at exit | 7,922,785.86 | 7,922,785.86 | 0.00 | PASS | Within tolerance. |
| Upside | Gross sale price | 102,229,494.93 | 102,229,494.93 | 0.00 | PASS | Within tolerance. |
| Upside | Loan payoff at exit | 44,333,521.61 | 44,291,252.45 | -42,269.16 | FAIL | Workbook error: reproduced only by switching on #1 Payoff uses the exit month's opening balance (Monthly CF row 73, D73:CU73) |
| Upside | Net sale proceeds to equity | 54,829,088.47 | 54,871,357.63 | 42,269.16 | FAIL | Workbook error: reproduced only by switching on #1 Payoff uses the exit month's opening balance (Monthly CF row 73, D73:CU73) |
| Upside | Unlevered IRR | 18.3020% | 18.3020% | -0.0000 pp | PASS | Within tolerance. |
| Upside | Levered IRR | 32.2786% | 32.3000% | 0.0214 pp | FAIL | Workbook error: reproduced only by switching on #1 Payoff uses the exit month's opening balance (Monthly CF row 73, D73:CU73) |
| Upside | Levered equity multiple | 2.7055x | 2.7072x | 0.0017 | FAIL | Workbook error: reproduced only by switching on #1 Payoff uses the exit month's opening balance (Monthly CF row 73, D73:CU73) |
| Upside | LP IRR | 28.5527% | 28.5376% | -0.0151 pp | FAIL | Workbook error: reproduced only by switching on #1 Payoff uses the exit month's opening balance (Monthly CF row 73, D73:CU73); #2 Pref accrues at 1 + pref/12 (Monthly CF B37, used by row 87) |
| Upside | GP IRR | 56.3262% | 56.5242% | 0.1980 pp | FAIL | Workbook error: reproduced only by switching on #1 Payoff uses the exit month's opening balance (Monthly CF row 73, D73:CU73); #2 Pref accrues at 1 + pref/12 (Monthly CF B37, used by row 87) |
| Upside | GP multiple | 5.2386x | 5.2653x | 0.0267 | FAIL | Workbook error: reproduced only by switching on #1 Payoff uses the exit month's opening balance (Monthly CF row 73, D73:CU73); #2 Pref accrues at 1 + pref/12 (Monthly CF B37, used by row 87) |
| Upside | Check: loan balance after exit (must be 0) | -42,269.16 | 0.00 | 42,269.16 | FAIL | Workbook error: reproduced only by switching on #1 Payoff uses the exit month's opening balance (Monthly CF row 73, D73:CU73) |
| Downside | Loan commitment | 41,850,000.00 | 41,850,000.00 | 0.00 | PASS | Within tolerance. |
| Downside | Initial funded loan (after PIP holdback) | 35,970,000.00 | 35,970,000.00 | 0.00 | PASS | Within tolerance. |
| Downside | Equity required at close | 28,718,500.00 | 28,718,500.00 | 0.00 | PASS | Within tolerance. |
| Downside | 2028 rooms revenue | 14,724,838.80 | 14,724,838.80 | -0.00 | PASS | Within tolerance. |
| Downside | 2028 total revenue | 20,760,663.36 | 20,760,663.36 | -0.00 | PASS | Within tolerance. |
| Downside | 2029 total departmental expenses | 7,837,624.95 | 7,737,580.29 | -100,044.67 | FAIL | Workbook error: reproduced only by switching on #3 F&B expense driven by rooms revenue 2029+ (Proforma - Downside E26:L26) |
| Downside | 2029 total undistributed expenses | 6,426,672.69 | 6,426,672.69 | -0.00 | PASS | Within tolerance. |
| Downside | 2029 NOI | 4,704,349.11 | 4,804,393.77 | 100,044.67 | FAIL | Workbook error: reproduced only by switching on #3 F&B expense driven by rooms revenue 2029+ (Proforma - Downside E26:L26) |
| Downside | Month 1 interest | 232,306.25 | 232,306.25 | 0.00 | PASS | Within tolerance. |
| Downside | First amortizing payment | 291,882.43 | 291,882.43 | -0.00 | PASS | Within tolerance. |
| Downside | Peak loan balance | 41,754,607.86 | 41,754,607.86 | 0.00 | PASS | Within tolerance. |
| Downside | Forward 12-month NOI at exit | 4,626,422.97 | 5,004,823.48 | 378,400.51 | FAIL | Workbook error: reproduced only by switching on #3 F&B expense driven by rooms revenue 2029+ (Proforma - Downside E26:L26) |
| Downside | Gross sale price | 52,873,405.39 | 57,197,982.67 | 4,324,577.28 | FAIL | Workbook error: reproduced only by switching on #3 F&B expense driven by rooms revenue 2029+ (Proforma - Downside E26:L26) |
| Downside | Loan payoff at exit | 39,594,124.41 | 39,562,618.33 | -31,506.09 | FAIL | Workbook error: reproduced only by switching on #1 Payoff uses the exit month's opening balance (Monthly CF row 73, D73:CU73) |
| Downside | Net sale proceeds to equity | 11,428,711.79 | 15,633,434.95 | 4,204,723.16 | FAIL | Workbook error: reproduced only by switching on #1 Payoff uses the exit month's opening balance (Monthly CF row 73, D73:CU73); #3 F&B expense driven by rooms revenue 2029+ (Proforma - Downside E26:L26) |
| Downside | Unlevered IRR | 2.7380% | 3.8762% | 1.1382 pp | FAIL | Workbook error: reproduced only by switching on #3 F&B expense driven by rooms revenue 2029+ (Proforma - Downside E26:L26) |
| Downside | Levered IRR | -8.0564% | -3.8510% | 4.2054 pp | FAIL | Workbook error: reproduced only by switching on #1 Payoff uses the exit month's opening balance (Monthly CF row 73, D73:CU73); #3 F&B expense driven by rooms revenue 2029+ (Proforma - Downside E26:L26) |
| Downside | Levered equity multiple | 0.5993x | 0.7850x | 0.1857 | FAIL | Workbook error: reproduced only by switching on #1 Payoff uses the exit month's opening balance (Monthly CF row 73, D73:CU73); #3 F&B expense driven by rooms revenue 2029+ (Proforma - Downside E26:L26) |
| Downside | LP IRR | -8.0564% | -3.8510% | 4.2054 pp | FAIL | Workbook error: reproduced only by switching on #1 Payoff uses the exit month's opening balance (Monthly CF row 73, D73:CU73); #3 F&B expense driven by rooms revenue 2029+ (Proforma - Downside E26:L26) |
| Downside | GP IRR | -8.0564% | -3.8510% | 4.2054 pp | FAIL | Workbook error: reproduced only by switching on #1 Payoff uses the exit month's opening balance (Monthly CF row 73, D73:CU73); #3 F&B expense driven by rooms revenue 2029+ (Proforma - Downside E26:L26) |
| Downside | GP multiple | 0.5993x | 0.7850x | 0.1857 | FAIL | Workbook error: reproduced only by switching on #1 Payoff uses the exit month's opening balance (Monthly CF row 73, D73:CU73); #3 F&B expense driven by rooms revenue 2029+ (Proforma - Downside E26:L26) |
| Downside | Check: loan balance after exit (must be 0) | -31,506.09 | 0.00 | 31,506.09 | FAIL | Workbook error: reproduced only by switching on #1 Payoff uses the exit month's opening balance (Monthly CF row 73, D73:CU73) |

| Scenario | Workbook error | Levered IRR (spec) | Levered IRR (with error) | Levered impact (pp) | LP IRR impact (pp) | GP IRR impact (pp) |
|---|---|---:|---:|---:|---:|---:|
| Base | #1 Payoff uses the exit month's opening balance (Monthly CF row 73, D73:CU73) | 18.3860% | 18.3677% | -0.0183 | -0.0156 | -0.0336 |
| Base | #2 Pref accrues at 1 + pref/12 (Monthly CF B37, used by row 87) | 18.3860% | 18.3860% | 0.0000 | +0.0432 | -0.2397 |
| Base | All errors together (replicates the workbook) | 18.3860% | 18.3677% | -0.0183 | +0.0276 | -0.2735 |
| Upside | #1 Payoff uses the exit month's opening balance (Monthly CF row 73, D73:CU73) | 32.3000% | 32.2786% | -0.0214 | -0.0188 | -0.0350 |
| Upside | #2 Pref accrues at 1 + pref/12 (Monthly CF B37, used by row 87) | 32.3000% | 32.3000% | 0.0000 | +0.0339 | -0.1628 |
| Upside | All errors together (replicates the workbook) | 32.3000% | 32.2786% | -0.0214 | +0.0151 | -0.1980 |
| Downside | #1 Payoff uses the exit month's opening balance (Monthly CF row 73, D73:CU73) | -3.8510% | -3.8733% | -0.0224 | -0.0224 | -0.0224 |
| Downside | #2 Pref accrues at 1 + pref/12 (Monthly CF B37, used by row 87) | -3.8510% | -3.8510% | 0.0000 | 0.0000 | 0.0000 |
| Downside | #3 F&B expense driven by rooms revenue 2029+ (Proforma - Downside E26:L26) | -3.8510% | -8.0272% | -4.1762 | -4.1762 | -4.1762 |
| Downside | All errors together (replicates the workbook) | -3.8510% | -8.0564% | -4.2054 | -4.2054 | -4.2054 |
