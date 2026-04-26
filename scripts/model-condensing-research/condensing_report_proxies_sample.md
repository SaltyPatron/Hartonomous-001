# Model condensing research report
Generated: 2026-04-26T20:31:48.008641+00:00Z

## Denominator: B_raw (exact)
- **Root:** `D:\Models\hub\Conditional-DETR-R50`
- **Total** `B_raw` over processed snapshots: **174,168,748** bytes (0.174 GB)

## Numerator (see METRICS.md)
- `B_distilled` (true export bytes): **not computable** here; requires your packer + thresholds.
- **Proxy:** magnitude-epsilon sparsity, row L2, SVD 90% energy; NOT equivalent to spec functional sparsity.

## Per snapshot

| Snapshot | B_raw (bytes) | tensors | #params (elements) |
|---|---:|---:|---:|
| `D:\Models\hub\Conditional-DETR-R50` | 174,168,748 | 592 | 43,524,645 |

### Byte share by bucket - `D:\Models\hub\Conditional-DETR-R50`

| bucket | bytes | % of B_raw in snapshot |
|---|---:|---:|
| other | 96,016,128 | 55.13 |
| track2_other2d | 63,963,136 | 36.72 |
| track2_attn | 13,107,200 | 7.53 |
| other_small | 704,932 | 0.40 |
| track1_pos_emb | 307,200 | 0.18 |

## Proxy details (first 80 rows, full set in out file JSON if needed)
```
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'bbox_predictor.layers.0.bias', 'bucket': 'other_small', 'eps': 1e-09, 'frac_lt_eps': 0.0, 'n_sample': 256}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'bbox_predictor.layers.0.bias', 'bucket': 'other_small', 'eps': 1e-05, 'frac_lt_eps': 0.0, 'n_sample': 256}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'bbox_predictor.layers.0.bias', 'bucket': 'other_small', 'eps': 0.001, 'frac_lt_eps': 0.015625, 'n_sample': 256}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'bbox_predictor.layers.0.weight', 'bucket': 'track2_other2d', 'eps': 1e-09, 'frac_lt_eps': 0.0, 'n_sample': 65536}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'bbox_predictor.layers.0.weight', 'bucket': 'track2_other2d', 'eps': 1e-05, 'frac_lt_eps': 0.0001220703125, 'n_sample': 65536}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'bbox_predictor.layers.0.weight', 'bucket': 'track2_other2d', 'eps': 0.001, 'frac_lt_eps': 0.010589599609375, 'n_sample': 65536}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'bbox_predictor.layers.0.weight', 'bucket': 'track2_other2d', 'per_row_l2_thr': 1e-09, 'row_frac_l2_below': 0.0}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'bbox_predictor.layers.0.weight', 'svd_k90_energy_0.9': 107, 'svd_k99_energy_0.99': 184, 'dense_f32_B': 262144, 'lowrank_90_f32_B': 219136, 'lowrank_90_over_dense': 0.8359}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'bbox_predictor.layers.1.bias', 'bucket': 'other_small', 'eps': 1e-09, 'frac_lt_eps': 0.0, 'n_sample': 256}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'bbox_predictor.layers.1.bias', 'bucket': 'other_small', 'eps': 1e-05, 'frac_lt_eps': 0.0, 'n_sample': 256}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'bbox_predictor.layers.1.bias', 'bucket': 'other_small', 'eps': 0.001, 'frac_lt_eps': 0.01171875, 'n_sample': 256}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'bbox_predictor.layers.1.weight', 'bucket': 'track2_other2d', 'eps': 1e-09, 'frac_lt_eps': 0.0, 'n_sample': 65536}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'bbox_predictor.layers.1.weight', 'bucket': 'track2_other2d', 'eps': 1e-05, 'frac_lt_eps': 9.1552734375e-05, 'n_sample': 65536}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'bbox_predictor.layers.1.weight', 'bucket': 'track2_other2d', 'eps': 0.001, 'frac_lt_eps': 0.010986328125, 'n_sample': 65536}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'bbox_predictor.layers.1.weight', 'bucket': 'track2_other2d', 'per_row_l2_thr': 1e-09, 'row_frac_l2_below': 0.0}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'bbox_predictor.layers.1.weight', 'svd_k90_energy_0.9': 115, 'svd_k99_energy_0.99': 189, 'dense_f32_B': 262144, 'lowrank_90_f32_B': 235520, 'lowrank_90_over_dense': 0.8984}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'bbox_predictor.layers.2.bias', 'bucket': 'other_small', 'eps': 1e-09, 'frac_lt_eps': 0.0, 'n_sample': 4}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'bbox_predictor.layers.2.bias', 'bucket': 'other_small', 'eps': 1e-05, 'frac_lt_eps': 0.0, 'n_sample': 4}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'bbox_predictor.layers.2.bias', 'bucket': 'other_small', 'eps': 0.001, 'frac_lt_eps': 0.0, 'n_sample': 4}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'bbox_predictor.layers.2.weight', 'bucket': 'other', 'eps': 1e-09, 'frac_lt_eps': 0.0, 'n_sample': 1024}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'bbox_predictor.layers.2.weight', 'bucket': 'other', 'eps': 1e-05, 'frac_lt_eps': 0.0, 'n_sample': 1024}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'bbox_predictor.layers.2.weight', 'bucket': 'other', 'eps': 0.001, 'frac_lt_eps': 0.15234375, 'n_sample': 1024}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'class_labels_classifier.bias', 'bucket': 'other_small', 'eps': 1e-09, 'frac_lt_eps': 0.0, 'n_sample': 91}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'class_labels_classifier.bias', 'bucket': 'other_small', 'eps': 1e-05, 'frac_lt_eps': 0.0, 'n_sample': 91}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'class_labels_classifier.bias', 'bucket': 'other_small', 'eps': 0.001, 'frac_lt_eps': 0.0, 'n_sample': 91}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'class_labels_classifier.weight', 'bucket': 'other', 'eps': 1e-09, 'frac_lt_eps': 0.0, 'n_sample': 23296}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'class_labels_classifier.weight', 'bucket': 'other', 'eps': 1e-05, 'frac_lt_eps': 8.585164835164836e-05, 'n_sample': 23296}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'class_labels_classifier.weight', 'bucket': 'other', 'eps': 0.001, 'frac_lt_eps': 0.011117788461538462, 'n_sample': 23296}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'model.backbone.conv_encoder.model.bn1.bias', 'bucket': 'other_small', 'eps': 1e-09, 'frac_lt_eps': 0.0, 'n_sample': 64}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'model.backbone.conv_encoder.model.bn1.bias', 'bucket': 'other_small', 'eps': 1e-05, 'frac_lt_eps': 0.015625, 'n_sample': 64}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'model.backbone.conv_encoder.model.bn1.bias', 'bucket': 'other_small', 'eps': 0.001, 'frac_lt_eps': 0.015625, 'n_sample': 64}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'model.backbone.conv_encoder.model.bn1.running_mean', 'bucket': 'other_small', 'eps': 1e-09, 'frac_lt_eps': 0.0, 'n_sample': 64}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'model.backbone.conv_encoder.model.bn1.running_mean', 'bucket': 'other_small', 'eps': 1e-05, 'frac_lt_eps': 0.03125, 'n_sample': 64}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'model.backbone.conv_encoder.model.bn1.running_mean', 'bucket': 'other_small', 'eps': 0.001, 'frac_lt_eps': 0.375, 'n_sample': 64}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'model.backbone.conv_encoder.model.bn1.running_var', 'bucket': 'other_small', 'eps': 1e-09, 'frac_lt_eps': 0.015625, 'n_sample': 64}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'model.backbone.conv_encoder.model.bn1.running_var', 'bucket': 'other_small', 'eps': 1e-05, 'frac_lt_eps': 0.015625, 'n_sample': 64}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'model.backbone.conv_encoder.model.bn1.running_var', 'bucket': 'other_small', 'eps': 0.001, 'frac_lt_eps': 0.015625, 'n_sample': 64}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'model.backbone.conv_encoder.model.bn1.weight', 'bucket': 'other_small', 'eps': 1e-09, 'frac_lt_eps': 0.0, 'n_sample': 64}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'model.backbone.conv_encoder.model.bn1.weight', 'bucket': 'other_small', 'eps': 1e-05, 'frac_lt_eps': 0.015625, 'n_sample': 64}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'model.backbone.conv_encoder.model.bn1.weight', 'bucket': 'other_small', 'eps': 0.001, 'frac_lt_eps': 0.015625, 'n_sample': 64}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'model.backbone.conv_encoder.model.conv1.weight', 'bucket': 'other', 'eps': 1e-09, 'frac_lt_eps': 0.00042517006802721087, 'n_sample': 9408}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'model.backbone.conv_encoder.model.conv1.weight', 'bucket': 'other', 'eps': 1e-05, 'frac_lt_eps': 0.015731292517006803, 'n_sample': 9408}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'model.backbone.conv_encoder.model.conv1.weight', 'bucket': 'other', 'eps': 0.001, 'frac_lt_eps': 0.035076530612244895, 'n_sample': 9408}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'model.backbone.conv_encoder.model.layer1.0.bn1.bias', 'bucket': 'other_small', 'eps': 1e-09, 'frac_lt_eps': 0.0, 'n_sample': 64}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'model.backbone.conv_encoder.model.layer1.0.bn1.bias', 'bucket': 'other_small', 'eps': 1e-05, 'frac_lt_eps': 0.078125, 'n_sample': 64}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'model.backbone.conv_encoder.model.layer1.0.bn1.bias', 'bucket': 'other_small', 'eps': 0.001, 'frac_lt_eps': 0.078125, 'n_sample': 64}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'model.backbone.conv_encoder.model.layer1.0.bn1.running_mean', 'bucket': 'other_small', 'eps': 1e-09, 'frac_lt_eps': 0.0, 'n_sample': 64}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'model.backbone.conv_encoder.model.layer1.0.bn1.running_mean', 'bucket': 'other_small', 'eps': 1e-05, 'frac_lt_eps': 0.078125, 'n_sample': 64}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'model.backbone.conv_encoder.model.layer1.0.bn1.running_mean', 'bucket': 'other_small', 'eps': 0.001, 'frac_lt_eps': 0.078125, 'n_sample': 64}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'model.backbone.conv_encoder.model.layer1.0.bn1.running_var', 'bucket': 'other_small', 'eps': 1e-09, 'frac_lt_eps': 0.078125, 'n_sample': 64}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'model.backbone.conv_encoder.model.layer1.0.bn1.running_var', 'bucket': 'other_small', 'eps': 1e-05, 'frac_lt_eps': 0.078125, 'n_sample': 64}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'model.backbone.conv_encoder.model.layer1.0.bn1.running_var', 'bucket': 'other_small', 'eps': 0.001, 'frac_lt_eps': 0.078125, 'n_sample': 64}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'model.backbone.conv_encoder.model.layer1.0.bn1.weight', 'bucket': 'other_small', 'eps': 1e-09, 'frac_lt_eps': 0.0, 'n_sample': 64}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'model.backbone.conv_encoder.model.layer1.0.bn1.weight', 'bucket': 'other_small', 'eps': 1e-05, 'frac_lt_eps': 0.078125, 'n_sample': 64}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'model.backbone.conv_encoder.model.layer1.0.bn1.weight', 'bucket': 'other_small', 'eps': 0.001, 'frac_lt_eps': 0.078125, 'n_sample': 64}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'model.backbone.conv_encoder.model.layer1.0.bn2.bias', 'bucket': 'other_small', 'eps': 1e-09, 'frac_lt_eps': 0.0, 'n_sample': 64}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'model.backbone.conv_encoder.model.layer1.0.bn2.bias', 'bucket': 'other_small', 'eps': 1e-05, 'frac_lt_eps': 0.046875, 'n_sample': 64}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'model.backbone.conv_encoder.model.layer1.0.bn2.bias', 'bucket': 'other_small', 'eps': 0.001, 'frac_lt_eps': 0.046875, 'n_sample': 64}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'model.backbone.conv_encoder.model.layer1.0.bn2.running_mean', 'bucket': 'other_small', 'eps': 1e-09, 'frac_lt_eps': 0.0, 'n_sample': 64}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'model.backbone.conv_encoder.model.layer1.0.bn2.running_mean', 'bucket': 'other_small', 'eps': 1e-05, 'frac_lt_eps': 0.046875, 'n_sample': 64}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'model.backbone.conv_encoder.model.layer1.0.bn2.running_mean', 'bucket': 'other_small', 'eps': 0.001, 'frac_lt_eps': 0.0625, 'n_sample': 64}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'model.backbone.conv_encoder.model.layer1.0.bn2.running_var', 'bucket': 'other_small', 'eps': 1e-09, 'frac_lt_eps': 0.046875, 'n_sample': 64}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'model.backbone.conv_encoder.model.layer1.0.bn2.running_var', 'bucket': 'other_small', 'eps': 1e-05, 'frac_lt_eps': 0.046875, 'n_sample': 64}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'model.backbone.conv_encoder.model.layer1.0.bn2.running_var', 'bucket': 'other_small', 'eps': 0.001, 'frac_lt_eps': 0.0625, 'n_sample': 64}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'model.backbone.conv_encoder.model.layer1.0.bn2.weight', 'bucket': 'other_small', 'eps': 1e-09, 'frac_lt_eps': 0.0, 'n_sample': 64}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'model.backbone.conv_encoder.model.layer1.0.bn2.weight', 'bucket': 'other_small', 'eps': 1e-05, 'frac_lt_eps': 0.046875, 'n_sample': 64}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'model.backbone.conv_encoder.model.layer1.0.bn2.weight', 'bucket': 'other_small', 'eps': 0.001, 'frac_lt_eps': 0.046875, 'n_sample': 64}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'model.backbone.conv_encoder.model.layer1.0.bn3.bias', 'bucket': 'other_small', 'eps': 1e-09, 'frac_lt_eps': 0.0, 'n_sample': 256}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'model.backbone.conv_encoder.model.layer1.0.bn3.bias', 'bucket': 'other_small', 'eps': 1e-05, 'frac_lt_eps': 0.0625, 'n_sample': 256}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'model.backbone.conv_encoder.model.layer1.0.bn3.bias', 'bucket': 'other_small', 'eps': 0.001, 'frac_lt_eps': 0.10546875, 'n_sample': 256}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'model.backbone.conv_encoder.model.layer1.0.bn3.running_mean', 'bucket': 'other_small', 'eps': 1e-09, 'frac_lt_eps': 0.0, 'n_sample': 256}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'model.backbone.conv_encoder.model.layer1.0.bn3.running_mean', 'bucket': 'other_small', 'eps': 1e-05, 'frac_lt_eps': 0.06640625, 'n_sample': 256}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'model.backbone.conv_encoder.model.layer1.0.bn3.running_mean', 'bucket': 'other_small', 'eps': 0.001, 'frac_lt_eps': 0.125, 'n_sample': 256}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'model.backbone.conv_encoder.model.layer1.0.bn3.running_var', 'bucket': 'other_small', 'eps': 1e-09, 'frac_lt_eps': 0.078125, 'n_sample': 256}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'model.backbone.conv_encoder.model.layer1.0.bn3.running_var', 'bucket': 'other_small', 'eps': 1e-05, 'frac_lt_eps': 0.1015625, 'n_sample': 256}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'model.backbone.conv_encoder.model.layer1.0.bn3.running_var', 'bucket': 'other_small', 'eps': 0.001, 'frac_lt_eps': 0.28515625, 'n_sample': 256}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'model.backbone.conv_encoder.model.layer1.0.bn3.weight', 'bucket': 'other_small', 'eps': 1e-09, 'frac_lt_eps': 0.0, 'n_sample': 256}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'model.backbone.conv_encoder.model.layer1.0.bn3.weight', 'bucket': 'other_small', 'eps': 1e-05, 'frac_lt_eps': 0.0703125, 'n_sample': 256}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'model.backbone.conv_encoder.model.layer1.0.bn3.weight', 'bucket': 'other_small', 'eps': 0.001, 'frac_lt_eps': 0.125, 'n_sample': 256}
{'file': 'D:/Models/hub/Conditional-DETR-R50/model.safetensors', 'tensor': 'model.backbone.conv_encoder.model.layer1.0.conv1.weight', 'bucket': 'other', 'eps': 1e-09, 'frac_lt_eps': 0.006591796875, 'n_sample': 4096}
... 1936 more (run with --out to keep full text)
```
