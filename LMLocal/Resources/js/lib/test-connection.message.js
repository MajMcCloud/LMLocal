"use strict";

import { UIText, V1_TIP } from '@app/constants/app.globals.js';

/**
 * Builds the toast text for a failed provider "Test connection" probe.
 */
export function buildTestFailureMessage(result, fallbackUrl = '') {
    const error = result?.error;
    const base = (error && typeof error === 'object' ? error.message : error) || UIText.CONNECTION_TEST_FAILED;

    const url = result?.url || fallbackUrl || '';

    let message = base;
    if (url) {
        message += ` — ${url}`;
    }
    if (url.includes('/v1')) {
        message += ` ${V1_TIP}`;
    }
    return message;
}
