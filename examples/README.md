# Пример для первого запуска

Добавьте `sample-letter.png`, `sample-letter.jpg` или `sample-letter.pdf` через «Добавить файлы», распознайте и сравните текст с `sample-letter.txt`. PDF содержит изображение, а не готовый текстовый слой. Для автоматической проверки используйте `sample-letter.reference.json`.

PNG можно пересоздать скриптом `generate_sample.py` при наличии Pillow и шрифта Arial в Windows. JPEG и PDF создаются из PNG скриптом `generate_sample_variants.py` при наличии Pillow и pypdf. Примеры не содержат реальных документов или личных данных.
