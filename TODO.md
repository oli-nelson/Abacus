# TODO

- [ ] Make latest-comment polling more efficient when the project uses a shared
  Dolt server. Query the latest comments directly with SQL instead of parsing a
  full `bd --readonly export`, while retaining the export path as a fallback for
  local or embedded Beads storage. The SQL path must preserve the configured
  comment limit, newest-first ordering, issue titles, authors, and user-attention
  label state.
