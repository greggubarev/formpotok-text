# Происхождение встроенных моделей

Версии и SHA-256 весов для Windows-пакета зафиксированы в [`packaging/model-lock.json`](../packaging/model-lock.json). Сборка сверяет локальные файлы с lock без доступа к сети; метаданные загрузки содержат ревизию, а карточки в кэше объявляют лицензию `apache-2.0`. Ссылки ниже ведут на конкретные ревизии официальных репозиториев моделей. SHA-256 весов дополнительно сверены с данными файлов официального API Hugging Face на 13 октября 2026 года.

| Модель | Ревизия | SHA-256 `inference.pdiparams` |
| --- | --- | --- |
| [PP-OCRv5_mobile_det](https://huggingface.co/PaddlePaddle/PP-OCRv5_mobile_det/tree/0d63e78e2b680928f6b1747d76a08db6e645efb7) | `0d63e78e2b680928f6b1747d76a08db6e645efb7` | `afa1820cb16c1fd0dad589d0f8b389139061c1ef6d68019685fd07be997dda5b` |
| [eslav_PP-OCRv5_mobile_rec](https://huggingface.co/PaddlePaddle/eslav_PP-OCRv5_mobile_rec/tree/7553801264d3379d8d2e854971989e5e26c22e03) | `7553801264d3379d8d2e854971989e5e26c22e03` | `f11057b05d8517868bca505271278973d706600d9dcc184cbcf5c4512091c32b` |
| [PP-LCNet_x1_0_doc_ori](https://huggingface.co/PaddlePaddle/PP-LCNet_x1_0_doc_ori/tree/d3b95a6dff5fe8a94f2748e12b61cb26818a0df8) | `d3b95a6dff5fe8a94f2748e12b61cb26818a0df8` | `e8d6e7c5d264507e40e58a655779059d616b20d7441ea22047d829eb3931989c` |
| [UVDoc](https://huggingface.co/PaddlePaddle/UVDoc/tree/16c3f0ea9c2f0c6a57e24160f7eeaa7574613fa3) | `16c3f0ea9c2f0c6a57e24160f7eeaa7574613fa3` | `810488899520e0da843b9bd9769ba4949f1c81e357f0eceb12d4a7da459c3eca` |
| [PP-LCNet_x1_0_textline_ori](https://huggingface.co/PaddlePaddle/PP-LCNet_x1_0_textline_ori/tree/cd237a44b0e359d4fe38310a416203cf7403faa5) | `cd237a44b0e359d4fe38310a416203cf7403faa5` | `0de2bcf996cf553e2b848dd7b1769dafffc6917b1ccdf55c1d8efe7909fbf743` |

Проверка lock подтверждает тождество включённых весов указанным файлам и заявленную лицензию карточек. Она не заменяет отдельную проверку условий распространения всех библиотек и наличия требуемых текстов лицензий перед публичным выпуском.
