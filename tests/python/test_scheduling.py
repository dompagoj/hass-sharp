import importlib.util
import unittest
from datetime import datetime, timezone

spec = importlib.util.spec_from_file_location('scheduling', 'custom_components/hass_sharp/scheduling.py')
s = importlib.util.module_from_spec(spec)
spec.loader.exec_module(s)
def utc(text): return datetime.fromisoformat(text).replace(tzinfo=timezone.utc)

class Scheduling(unittest.TestCase):
    def test_daily_gap_fold_and_restart(self):
        rule = {'LocalTime': '02:30:00', 'TimeZone': 'Europe/Zagreb'}
        self.assertEqual(s.next_occurrence(rule, utc('2026-03-28T02:00:00')), utc('2026-03-30T00:30:00'))
        self.assertEqual(s.next_occurrence(rule, utc('2026-10-24T23:00:00')), utc('2026-10-25T00:30:00'))
        self.assertEqual(s.next_occurrence(rule, utc('2026-10-25T00:30:00')), utc('2026-10-26T01:30:00'))
        self.assertEqual(s.next_occurrence(rule, utc('2026-11-12T02:00:00')), utc('2026-11-13T01:30:00'))
    def test_one_shot_missed_and_offset(self):
        rule = {'AtUtc': '2026-11-12T12:00:00+00:00'}
        self.assertEqual(s.next_occurrence(rule, utc('2026-11-12T11:00:00')), utc('2026-11-12T12:00:00'))
        self.assertIsNone(s.next_occurrence(rule, utc('2026-11-12T12:00:00')))
    def test_registration_dedup_and_disposal(self):
        pending, calls = [], []
        now = utc('2026-11-12T11:00:00')
        def track(cb, when):
            row = [cb, when, False]; pending.append(row)
            return lambda: row.__setitem__(2, True)
        cancel = s.register({'LocalTime': '12:00:00', 'TimeZone': 'UTC'}, calls.append, track, lambda: now)
        callback, planned, _ = pending[0]
        now = planned
        callback(now); callback(now)
        self.assertEqual(len(calls), 1)
        self.assertEqual(len(pending), 2)
        cancel()
        self.assertTrue(pending[-1][2])
        pending[-1][0](pending[-1][1])
        self.assertEqual(len(calls), 1)
