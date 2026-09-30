-- x0 MultiPlay 로그인에 쓰는 회원 스키마 (x0_sample)
-- 서버가 읽는 컬럼: id, uid, password, sha_pwd, name, nickname, gender,
--                  deleted_at, blocked_at, last_login_at

CREATE DATABASE IF NOT EXISTS `x0_sample`
  DEFAULT CHARACTER SET utf8mb4
  COLLATE utf8mb4_unicode_ci;

USE `x0_sample`;

CREATE TABLE IF NOT EXISTS `app_users` (
  `id` BIGINT(20) UNSIGNED NOT NULL AUTO_INCREMENT,
  `uid` VARCHAR(255) NOT NULL,
  `name` VARCHAR(255) NOT NULL,
  `nickname` VARCHAR(255) NULL DEFAULT NULL,
  `profile_image_path` VARCHAR(512) NULL DEFAULT NULL,
  `email` VARCHAR(255) NOT NULL,
  `email_verified_at` TIMESTAMP NULL DEFAULT NULL,
  `password` VARCHAR(255) NOT NULL COMMENT '',
  `sha_pwd` VARCHAR(255) NULL DEFAULT NULL COMMENT 'SHA-256 hex',
  `is_password_set` TINYINT(1) NOT NULL DEFAULT 0,
  `gender` VARCHAR(255) NULL DEFAULT NULL,
  `country` VARCHAR(100) NULL DEFAULT NULL,
  `dial_code` VARCHAR(20) NULL DEFAULT NULL,
  `phone_number` VARCHAR(255) NULL DEFAULT NULL,
  `normalized_phone` VARCHAR(32)
    AS (
      REPLACE(
        REPLACE(
          REPLACE(
            REPLACE(COALESCE(`phone_number`, ''), '-', ''),
            ' ', ''
          ),
          '.', ''
        ),
        '+', ''
      )
    ) VIRTUAL,
  `status` VARCHAR(255) NOT NULL DEFAULT 'normal',
  `category` VARCHAR(255) NULL DEFAULT NULL,
  `ip` VARCHAR(255) NULL DEFAULT NULL,
  `memo` LONGTEXT NULL DEFAULT NULL,
  `last_login_at` TIMESTAMP NULL DEFAULT NULL,
  `joined_at` TIMESTAMP NULL DEFAULT NULL,
  `blocked_at` TIMESTAMP NULL DEFAULT NULL,
  `deleted_at` TIMESTAMP NULL DEFAULT NULL,
  `created_at` TIMESTAMP NULL DEFAULT NULL,
  `updated_at` TIMESTAMP NULL DEFAULT NULL,

  PRIMARY KEY (`id`),
  UNIQUE KEY `app_users_uid_unique` (`uid`),
  UNIQUE KEY `app_users_email_unique` (`email`),
  UNIQUE KEY `app_users_phone_number_unique` (`phone_number`),
  KEY `idx_app_users_normalized_phone` (`normalized_phone`)
) ENGINE=InnoDB
  DEFAULT CHARSET=utf8mb4
  COLLATE=utf8mb4_unicode_ci;
