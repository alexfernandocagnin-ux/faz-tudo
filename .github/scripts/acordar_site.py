"""Abre o site do Conserta SIA num navegador de verdade e, se o Streamlit Cloud tiver colocado o app para dormir,
clica no botão de acordar. Roda pelo GitHub Actions (.github/workflows/manter-acordado.yml).

Uma requisição simples (curl) não conta como uso para o Streamlit: é preciso abrir a página e esperar o app carregar.
"""

import os
import sys

from playwright.sync_api import TimeoutError as PlaywrightTimeout
from playwright.sync_api import sync_playwright

URL = os.environ.get("SITE_URL", "https://conserta-sia.streamlit.app/")
APP_TEXT = "Solucionador SIA"  # aparece quando o app carregou
WAKE_BUTTONS = ["Yes, get this app back up", "Sim, reative este aplicativo", "get this app back up"]


def main() -> int:
    with sync_playwright() as p:
        browser = p.chromium.launch()
        page = browser.new_page()
        print(f"Abrindo {URL}")
        page.goto(URL, wait_until="domcontentloaded", timeout=120_000)
        page.wait_for_timeout(10_000)

        # A página do Streamlit Cloud fica dentro de um iframe quando o app está rodando; a de "dormindo" não.
        for frame in [page, *page.frames]:
            for label in WAKE_BUTTONS:
                button = frame.get_by_role("button", name=label)
                if button.count() > 0:
                    print("O app estava dormindo: clicando para acordar.")
                    button.first.click()
                    break

        for attempt in range(18):  # até ~3 minutos para o app subir
            if any(f.get_by_text(APP_TEXT).count() > 0 for f in [page, *page.frames]):
                print("Site acordado e respondendo.")
                browser.close()
                return 0
            page.wait_for_timeout(10_000)

        print("O site não terminou de carregar em 3 minutos.", file=sys.stderr)
        page.screenshot(path="site.png", full_page=True)
        browser.close()
        return 1


if __name__ == "__main__":
    try:
        sys.exit(main())
    except PlaywrightTimeout as exc:
        print(f"Tempo esgotado ao abrir o site: {exc}", file=sys.stderr)
        sys.exit(1)
