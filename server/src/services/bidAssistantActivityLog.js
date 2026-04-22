import { BidAssistantActivity } from '../models/BidAssistantActivity.js';

/**
 * @param {object} doc
 * @param {string} [doc.groupId]
 * @param {string} doc.userId
 * @param {string} [doc.url]
 * @param {number} doc.httpStatus
 * @param {string} [doc.error]
 * @param {string} [doc.bidId]
 * @param {string} [doc.groupLinkId]
 * @param {object} [doc.meta]
 */
export async function persistBidAssistantActivity(doc) {
  try {
    await BidAssistantActivity.create(doc);
  } catch (e) {
    console.error('persistBidAssistantActivity failed', e);
  }
}
