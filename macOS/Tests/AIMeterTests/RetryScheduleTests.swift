// SPDX-FileCopyrightText: 2026 Matt Curfman and AI Meter contributors
// SPDX-License-Identifier: Apache-2.0

import Foundation
import Testing
@testable import AIMeter

@Test func retryDelaysGrowWithJitterAndResetAfterSuccess() {
    for random in [0.0, 0.5, 0.999] {
        var schedule = RetrySchedule()
        #expect(schedule.ready(0))
        for maximum in [600.0, 1200, 2400, 3600, 3600, 3600] {
            schedule.fail(1, random: random)
            #expect(schedule.nextAttempt == 1 + maximum / 2 + random * maximum / 2)
            #expect(!schedule.ready(schedule.nextAttempt - 1))
            #expect(schedule.ready(schedule.nextAttempt))
        }
        schedule.fail(1, retryAfter: 7200, random: random)
        #expect(schedule.nextAttempt == 7201)
        schedule.success(1)
        #expect(schedule.nextAttempt == 301)
        schedule.fail(1, random: 0)
        #expect(schedule.nextAttempt == 301)
        #expect(RetrySchedule().ready(0))
    }
}

@Test func retryAfterAcceptsSecondsAndDates() {
    let epoch = Date(timeIntervalSince1970: 0)
    #expect(RetrySchedule.retryAfter("7200") == 7200)
    for header in [nil, "invalid", "-1", "NaN", "Infinity", ""] as [String?] {
        #expect(RetrySchedule.retryAfter(header) == 0)
    }
    #expect(RetrySchedule.retryAfter("Thu, 01 Jan 1970 02:00:00 GMT", now: epoch) == 7200)
    #expect(RetrySchedule.retryAfter("Wed, 31 Dec 1969 23:00:00 GMT", now: epoch) == 0)
}
