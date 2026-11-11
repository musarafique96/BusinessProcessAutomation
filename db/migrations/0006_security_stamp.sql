-- 0006: security stamp on users. Sign-in cookies carry the stamp they were issued with. A password
-- reset changes the stamp, so every existing session for that account stops working on its next request.
-- Run as the migration owner.

ALTER TABLE users
  ADD COLUMN IF NOT EXISTS security_stamp uuid NOT NULL DEFAULT gen_random_uuid();
