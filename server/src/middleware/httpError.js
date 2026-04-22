/**
 * Express error handler: malformed JSON bodies and uncaught errors.
 * Place after all routes. Keeps API responses JSON-shaped for clients.
 */
export function errorHandler(err, _req, res, _next) {
  const isParse =
    err &&
    (err.type === 'entity.parse.failed' ||
      err instanceof SyntaxError ||
      (typeof err.message === 'string' && err.message.includes('JSON')));
  if (isParse) {
    return res.status(400).json({ error: 'Invalid JSON body' });
  }
  const status = Number(err.status) || Number(err.statusCode) || 500;
  if (status < 500) {
    return res.status(status).json({ error: err.message || 'Request error' });
  }
  console.error(err);
  return res.status(500).json({ error: 'Internal server error' });
}
