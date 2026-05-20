# Model condensing research report
Generated: 2026-04-26T20:31:18.264384+00:00Z

## Denominator: B_raw (exact)
- **Root:** `D:\Models\hub`
- **Total** `B_raw` over processed snapshots: **118,900,699,745** bytes (118.901 GB)

## Numerator (see METRICS.md)
- `B_distilled` (true export bytes): **not computable** here; requires your packer + thresholds.
- **Proxy:** magnitude-epsilon sparsity, row L2, SVD 90% energy; NOT equivalent to spec functional sparsity.

## Per snapshot

| Snapshot | B_raw (bytes) | tensors | #params (elements) |
|---|---:|---:|---:|
| `D:\Models\hub\Conditional-DETR-R50` | 174,168,748 | 592 | 43,524,645 |
| `D:\Models\hub\DETR-ResNet-101` | 242,797,616 | 789 | 60,675,364 |
| `D:\Models\hub\Florence-2-base` | 463,221,266 | 666 | 231,567,705 |
| `D:\Models\hub\Florence-2-large` | 1,553,563,458 | 918 | 776,721,497 |
| `D:\Models\hub\Grounding-DINO-Base` | 933,400,872 | 1206 | 232,810,880 |
| `D:\Models\hub\models--deepseek-ai--deepseek-coder-33b-instruct\snapshots\61dc97b922b13995e7f83b7c8397701dbf9cfd4c` | 66,686,048,200 | 561 | 33,342,991,360 |
| `D:\Models\hub\models--deepseek-ai--DeepSeek-Coder-V2-Lite-Instruct\snapshots\e434a23f91ba5b4923cf6c9d9a238eb4a08e3a11` | 31,413,626,609 | 5291 | 15,706,484,224 |
| `D:\Models\hub\models--ibm-granite--granite-speech-3.3-8b\snapshots\315afb31116c9b79dc15864d091e59ca6bf10cf9` | 17,433,872,976 | 1113 | 8,682,790,160 |

### Byte share by bucket - `D:\Models\hub\Conditional-DETR-R50`

| bucket | bytes | % of B_raw in snapshot |
|---|---:|---:|
| other | 96,016,128 | 55.13 |
| track2_other2d | 63,963,136 | 36.72 |
| track2_attn | 13,107,200 | 7.53 |
| other_small | 704,932 | 0.40 |
| track1_pos_emb | 307,200 | 0.18 |

### Byte share by bucket - `D:\Models\hub\DETR-ResNet-101`

| bucket | bytes | % of B_raw in snapshot |
|---|---:|---:|
| other | 171,774,720 | 70.75 |
| track2_other2d | 50,855,936 | 20.95 |
| track2_attn | 18,874,368 | 7.77 |
| other_small | 1,094,048 | 0.45 |
| track1_pos_emb | 102,400 | 0.04 |

### Byte share by bucket - `D:\Models\hub\Florence-2-base`

| bucket | bytes | % of B_raw in snapshot |
|---|---:|---:|
| track2_other2d | 360,846,848 | 77.90 |
| track2_attn | 84,934,656 | 18.34 |
| other | 13,221,810 | 2.85 |
| track1_pos_emb | 3,459,072 | 0.75 |
| other_small | 666,880 | 0.14 |
| track1_tok_emb | 6,144 | 0.00 |

### Byte share by bucket - `D:\Models\hub\Florence-2-large`

| bucket | bytes | % of B_raw in snapshot |
|---|---:|---:|
| track2_other2d | 1,181,927,424 | 76.08 |
| track2_attn | 301,989,888 | 19.44 |
| other | 50,589,362 | 3.26 |
| track1_pos_emb | 17,399,808 | 1.12 |
| other_small | 1,528,320 | 0.10 |
| track1_tok_emb | 8,192 | 0.00 |

### Byte share by bucket - `D:\Models\hub\Grounding-DINO-Base`

| bucket | bytes | % of B_raw in snapshot |
|---|---:|---:|
| track2_other2d | 613,154,816 | 65.69 |
| track2_attn | 202,899,456 | 21.74 |
| track1_tok_emb | 94,866,976 | 10.16 |
| other | 13,369,344 | 1.43 |
| track1_pos_emb | 7,271,392 | 0.78 |
| other_small | 1,672,192 | 0.18 |

### Byte share by bucket - `D:\Models\hub\models--deepseek-ai--deepseek-coder-33b-instruct\snapshots\61dc97b922b13995e7f83b7c8397701dbf9cfd4c`

| bucket | bytes | % of B_raw in snapshot |
|---|---:|---:|
| track2_ffn | 51,196,723,200 | 76.77 |
| track2_attn | 14,562,623,488 | 21.84 |
| track1_tok_emb | 462,422,016 | 0.69 |
| track2_other2d | 462,422,016 | 0.69 |
| other_small | 1,792,000 | 0.00 |

### Byte share by bucket - `D:\Models\hub\models--deepseek-ai--DeepSeek-Coder-V2-Lite-Instruct\snapshots\e434a23f91ba5b4923cf6c9d9a238eb4a08e3a11`

| bucket | bytes | % of B_raw in snapshot |
|---|---:|---:|
| track2_ffn | 29,830,676,480 | 94.96 |
| track2_other2d | 596,377,600 | 1.90 |
| track2_attn | 566,231,040 | 1.80 |
| track1_tok_emb | 419,430,400 | 1.34 |
| other_small | 252,928 | 0.00 |

### Byte share by bucket - `D:\Models\hub\models--ibm-granite--granite-speech-3.3-8b\snapshots\315afb31116c9b79dc15864d091e59ca6bf10cf9`

| bucket | bytes | % of B_raw in snapshot |
|---|---:|---:|
| track2_ffn | 13,119,782,912 | 75.25 |
| track2_attn | 3,550,478,336 | 20.37 |
| track1_tok_emb | 402,718,720 | 2.31 |
| other | 202,315,776 | 1.16 |
| track2_other2d | 156,569,600 | 0.90 |
| other_small | 1,872,512 | 0.01 |

