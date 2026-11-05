"""Turn an existing third-party CSV export into a provenance-rich review queue.

This tool reads local files only. It never requests a third-party website or writes CRM data.
"""

import argparse
import csv
import hashlib
import ipaddress
import json
import re
from collections import Counter
from datetime import datetime
from pathlib import Path
from urllib.parse import quote


def domain_of(raw: str) -> str:
    value = raw.strip().rstrip(".").lower()
    if not value or "/" in value or ":" in value or "\\" in value:
        raise ValueError("invalid domain")
    value = value.encode("idna").decode("ascii")
    if len(value) > 253 or "." not in value or any(
        not re.fullmatch(r"[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?", label)
        for label in value.split(".")
    ):
        raise ValueError("invalid domain")
    try:
        ipaddress.ip_address(value)
    except ValueError:
        return value
    raise ValueError("IP is not an enterprise domain")


def prepare(csv_path: Path, observed_at: str) -> dict:
    observed_time = datetime.fromisoformat(observed_at.replace("Z", "+00:00"))
    if observed_time.tzinfo is None or observed_time.utcoffset() is None:
        raise ValueError("observation time must include a timezone")
    source_hash = hashlib.sha256(csv_path.read_bytes()).hexdigest()
    with csv_path.open("r", encoding="utf-8-sig", newline="") as handle:
        rows = list(csv.DictReader(handle))
    required = {"域名", "结果", "公司名", "ICP备案号", "质检标记", "证据来源"}
    if not rows or not required.issubset(rows[0]):
        raise ValueError("CSV is empty or required columns are missing")

    seen = set()
    items = []
    for row_number, row in enumerate(rows, start=2):
        name = (row.get("公司名") or "").strip()
        licence = (row.get("ICP备案号") or "").strip()
        quality = (row.get("质检标记") or "").strip()
        try:
            domain = domain_of(row.get("域名") or "")
        except ValueError as exc:
            items.append({"row": row_number, "status": "quarantine", "reason": str(exc)})
            continue
        if domain in seen:
            items.append({"row": row_number, "domain": domain, "status": "quarantine", "reason": "duplicate domain"})
            continue
        seen.add(domain)
        status = "candidate" if name and not quality and "*" not in name else "quarantine" if name else "icp_only" if licence else "unresolved"
        claim = f"第三方页面汇总表显示主体名称：{name}" if name else "第三方页面汇总表未提供主体名称"
        if licence:
            claim += f"；页面汇总表所列备案号：{licence}"
        items.append({
            "row": row_number, "domain": domain, "status": status,
            "proposedName": name or None, "reportedIcpNumber": licence or None,
            "qualityFlag": quality or None, "originalResult": row.get("结果") or "",
            "originalSourceDescription": row.get("证据来源") or "",
            "evidence": {"sourceType": "third_party", "sourceReference": f"https://seo.chinaz.com/{quote(domain)}",
                         "claimText": claim, "observedAt": observed_at},
            "reason": "需核对原网页和独立来源" if status == "candidate" else
                      "名称异常，需人工核对" if status == "quarantine" else
                      "只有备案号，不能推断公司名称" if status == "icp_only" else "无主体线索",
        })
    return {"schemaVersion": 1, "sourceFile": str(csv_path.resolve()), "sourceSha256": source_hash,
            "sourceKind": "third_party_csv_unverified", "observedAt": observed_at,
            "counts": dict(Counter(item["status"] for item in items)), "items": items}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("csv", type=Path, help="Existing UTF-8 CSV export")
    parser.add_argument("--observed-at", required=True, help="Source observation time in ISO 8601 with timezone")
    parser.add_argument("--out", required=True, type=Path, help="Review queue JSON output")
    args = parser.parse_args()
    result = prepare(args.csv, args.observed_at)
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"rows": len(result["items"]), "counts": result["counts"], "out": str(args.out)}, ensure_ascii=False))


if __name__ == "__main__":
    main()
