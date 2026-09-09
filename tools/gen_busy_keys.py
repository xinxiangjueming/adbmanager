# -*- coding: utf-8 -*-
"""Append Busy_* overlay message keys to all language Resources.resw files."""
import io
import os

ROOT = r"D:\Windows-adb\src\AdbManager\Strings"

# key -> (zh-CN, en-US, ja, ko, de, fr, es)
ROWS = [
    ("Busy_Installing", "安装中，请稍候…", "Installing…", "インストール中…", "설치 중…", "Wird installiert…", "Installation…", "Instalando…"),
    ("Busy_ExtractingApk", "正在提取 APK…", "Extracting APK…", "APK を抽出中…", "APK 추출 중…", "APK wird extrahiert…", "Extraction de l'APK…", "Extrayendo APK…"),
    ("Busy_Screenshot", "正在截取屏幕…", "Taking screenshot…", "スクリーンショットを撮影中…", "화면 캡처 중…", "Screenshot wird erstellt…", "Capture d'écran…", "Capturando pantalla…"),
    ("Busy_SavingRecording", "正在保存录屏…", "Saving recording…", "録画を保存中…", "녹화 저장 중…", "Aufnahme wird gespeichert…", "Enregistrement de la vidéo…", "Guardando grabación…"),
    ("Busy_Uploading", "正在上传文件…", "Uploading file…", "ファイルをアップロード中…", "파일 업로드 중…", "Datei wird hochgeladen…", "Téléversement du fichier…", "Subiendo archivo…"),
    ("Busy_Downloading", "正在下载文件…", "Downloading file…", "ファイルをダウンロード中…", "ファイル 다운로드 중…", "Datei wird heruntergeladen…", "Téléchargement du fichier…", "Descargando archivo…"),
    ("Busy_Transferring", "正在传输文件…", "Transferring file…", "ファイルを転送中…", "파일 전송 중…", "Datei wird übertragen…", "Transfert du fichier…", "Transfiriendo archivo…"),
    ("Busy_Flashing", "正在刷入镜像，请勿断开设备…", "Flashing image, keep the device connected…", "イメージを書き込み中…", "이미지 플래싱 중…", "Image wird geflasht…", "Flash de l'image…", "Flasheando imagen…"),
    ("Busy_Erasing", "正在擦除分区…", "Erasing partition…", "パーティションを消去中…", "파티션 지우는 중…", "Partition wird gelöscht…", "Effacement de la partition…", "Borrando partición…"),
    ("Busy_ExtractingImage", "正在提取镜像…", "Extracting image…", "イメージを抽出中…", "이미지 추출 중…", "Image wird extrahiert…", "Extraction de l'image…", "Extrayendo imagen…"),
    ("Busy_Working", "正在执行…", "Working…", "処理中…", "작업 중…", "Wird ausgeführt…", "Traitement…", "Procesando…"),
    ("Busy_LoadingPartitions", "正在读取分区表…", "Reading partition table…", "パーティション表を読み込み中…", "파티션 테이블 읽는 중…", "Partitionstabelle wird gelesen…", "Lecture de la table de partitions…", "Leyendo tabla de particiones…"),
    ("Busy_LoadingFiles", "正在读取目录…", "Loading folder…", "フォルダを読み込み中…", "폴더 읽는 중…", "Ordner wird gelesen…", "Lecture du dossier…", "Leyendo carpeta…"),
]

LANGS = ["zh-CN", "en-US", "ja", "ko", "de", "fr", "es"]


def escape(value: str) -> str:
    return value.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


for index, lang in enumerate(LANGS):
    path = os.path.join(ROOT, lang, "Resources.resw")
    text = io.open(path, encoding="utf-8").read()
    added = 0
    for row in ROWS:
        key = row[0]
        if f'name="{key}"' in text:
            continue
        value = escape(row[1 + index])
        entry = f'  <data name="{key}" xml:space="preserve"><value>{value}</value></data>\n'
        text = text.replace("</root>", entry + "</root>")
        added += 1
    io.open(path, "w", encoding="utf-8", newline="").write(text)
    print(f"{lang}: +{added}")
