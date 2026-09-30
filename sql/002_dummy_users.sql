-- 테스트용 더미 회원. 비밀번호는 전부 1111
-- sha_pwd  = SHA-256("1111")
-- password = bcrypt("1111"), Laravel $2y$ 형식
-- 여러 번 돌려도 uid 기준으로 덮어씀

USE `x0_sample`;

SET @pw  = '$2y$10$9mQquG1zXkA0coyWt3JBPevf7obTHscoREDCh4k15eZjiKjTFgEVO';
SET @sha = '0ffe1abd1a08215353c233d6e009613e95eec4253832a761af28ff37ac5a150c';

INSERT INTO `app_users`
  (`uid`, `name`, `nickname`, `email`, `password`, `sha_pwd`, `is_password_set`,
   `gender`, `country`, `dial_code`, `phone_number`, `status`, `category`,
   `joined_at`, `blocked_at`, `deleted_at`, `created_at`, `updated_at`)
VALUES
  ('boy1',      '김민준', '소년1',   'boy1@example.com',      @pw, @sha, 1, 'M', 'KR', '+82', '010-1000-0001', 'normal',  'test', NOW(), NULL,  NULL,  NOW(), NOW()),
  ('boy2',      '이서준', '소년2',   'boy2@example.com',      @pw, @sha, 1, 'M', 'KR', '+82', '010-1000-0002', 'normal',  'test', NOW(), NULL,  NULL,  NOW(), NOW()),
  ('girl1',     '박지우', '소녀1',   'girl1@example.com',     @pw, @sha, 1, 'F', 'KR', '+82', '010-1000-0003', 'normal',  'test', NOW(), NULL,  NULL,  NOW(), NOW()),
  ('girl2',     '최서연', '소녀2',   'girl2@example.com',     @pw, @sha, 1, 'F', 'KR', '+82', '010-1000-0004', 'normal',  'test', NOW(), NULL,  NULL,  NOW(), NOW()),
  ('user01',    '정도윤', NULL,      'user01@example.com',    @pw, @sha, 1, 'M', 'KR', '+82', '010-1000-0005', 'normal',  NULL,   NOW(), NULL,  NULL,  NOW(), NOW()),
  ('user02',    '강하은', '하은',    'user02@example.com',    @pw, @sha, 1, 'F', 'KR', '+82', '010-1000-0006', 'normal',  NULL,   NOW(), NULL,  NULL,  NOW(), NOW()),
  ('user03',    '조예준', '예준',    'user03@example.com',    @pw, @sha, 1, 'M', 'KR', '+82', NULL,            'normal',  NULL,   NOW(), NULL,  NULL,  NOW(), NOW()),
  ('user04',    '윤수아', NULL,      'user04@example.com',    @pw, @sha, 1, NULL,'KR', '+82', NULL,            'normal',  NULL,   NOW(), NULL,  NULL,  NOW(), NOW()),
  ('blocked01', '차단됨', '차단',    'blocked01@example.com', @pw, @sha, 1, 'M', 'KR', '+82', '010-1000-0098', 'blocked', 'test', NOW(), NOW(), NULL,  NOW(), NOW()),
  ('deleted01', '삭제됨', '삭제',    'deleted01@example.com', @pw, @sha, 1, 'F', 'KR', '+82', '010-1000-0099', 'deleted', 'test', NOW(), NULL,  NOW(), NOW(), NOW())
ON DUPLICATE KEY UPDATE
  `name` = VALUES(`name`),
  `nickname` = VALUES(`nickname`),
  `password` = VALUES(`password`),
  `sha_pwd` = VALUES(`sha_pwd`),
  `is_password_set` = VALUES(`is_password_set`),
  `gender` = VALUES(`gender`),
  `status` = VALUES(`status`),
  `blocked_at` = VALUES(`blocked_at`),
  `deleted_at` = VALUES(`deleted_at`),
  `updated_at` = NOW();
