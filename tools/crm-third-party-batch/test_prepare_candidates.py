import csv
import tempfile
import unittest
from pathlib import Path

from prepare_candidates import prepare


class PrepareCandidatesTests(unittest.TestCase):
    def test_rejects_observation_time_without_timezone(self):
        with tempfile.TemporaryDirectory() as root:
            path = Path(root) / "source.csv"
            path.write_text("域名,结果,公司名,ICP备案号,质检标记,证据来源\n", encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "timezone"):
                prepare(path, "2026-10-23T00:00:00")

    def test_flags_unknown_and_masked_names_without_inventing_identity(self):
        with tempfile.TemporaryDirectory() as root:
            path = Path(root) / "source.csv"
            with path.open("w", encoding="utf-8", newline="") as handle:
                writer = csv.DictWriter(handle, fieldnames=["域名", "结果", "公司名", "ICP备案号", "质检标记", "证据来源"])
                writer.writeheader()
                writer.writerow({"域名": "example.com", "结果": "有公司名", "公司名": "甲公司", "ICP备案号": "京ICP备1号", "证据来源": "网页"})
                writer.writerow({"域名": "other.com", "结果": "有备案号", "ICP备案号": "鲁ICP备2号"})
                writer.writerow({"域名": "masked.com", "结果": "有公司名", "公司名": "乙**公司", "质检标记": "掩码"})
            result = prepare(path, "2026-10-23T00:00:00+08:00")
            self.assertEqual({"candidate": 1, "icp_only": 1, "quarantine": 1}, result["counts"])
            self.assertEqual("third_party", result["items"][0]["evidence"]["sourceType"])
            self.assertIsNone(result["items"][1]["proposedName"])
            self.assertEqual("quarantine", result["items"][2]["status"])


if __name__ == "__main__":
    unittest.main()
