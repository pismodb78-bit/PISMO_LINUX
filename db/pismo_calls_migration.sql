-- ============================================================
--  PISMO — таблицы звонков (дополнение к bdauth)
--  Схема совместима с Windows-версией (CallForm / CallTransport).
--  Выполнить в phpMyAdmin/консоли на базе bdauth.
-- ============================================================

CREATE TABLE IF NOT EXISTS `call_sessions` (
  `id`            INT(11) UNSIGNED NOT NULL AUTO_INCREMENT,
  `caller_id`     INT(10) UNSIGNED NOT NULL,
  `callee_id`     INT(10) UNSIGNED          DEFAULT NULL,
  `group_id`      INT(10) UNSIGNED          DEFAULT NULL,
  `status`        VARCHAR(16)      NOT NULL DEFAULT 'ringing',  -- ringing|active|ended|rejected|missed
  `has_video`     TINYINT(1)       NOT NULL DEFAULT 0,
  `has_screen`    TINYINT(1)       NOT NULL DEFAULT 0,

  -- Основной обмен SDP/ICE
  `caller_sdp`    LONGTEXT                  DEFAULT NULL,
  `callee_sdp`    LONGTEXT                  DEFAULT NULL,
  `caller_ice`    LONGTEXT                  DEFAULT NULL,       -- JSON-массив строк-кандидатов
  `callee_ice`    LONGTEXT                  DEFAULT NULL,

  -- Перепереговоры (renegotiation) для включения камеры/экрана в процессе звонка
  `renego_version`             INT(11)     NOT NULL DEFAULT 0,
  `caller_renego_offer`        LONGTEXT     DEFAULT NULL,
  `callee_renego_offer`        LONGTEXT     DEFAULT NULL,
  `caller_renego_answer`       LONGTEXT     DEFAULT NULL,
  `callee_renego_answer`       LONGTEXT     DEFAULT NULL,
  `caller_renego_offer_kinds`  TEXT         DEFAULT NULL,
  `callee_renego_offer_kinds`  TEXT         DEFAULT NULL,
  `caller_renego_answer_kinds` TEXT         DEFAULT NULL,
  `callee_renego_answer_kinds` TEXT         DEFAULT NULL,

  `created_at`    DATETIME         NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `ended_at`      DATETIME                  DEFAULT NULL,

  PRIMARY KEY (`id`),
  KEY `idx_callee_status` (`callee_id`, `status`),
  KEY `idx_caller_status` (`caller_id`, `status`),
  KEY `idx_group`         (`group_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Участники (для групповых звонков)
CREATE TABLE IF NOT EXISTS `call_participants` (
  `id`         INT(11) UNSIGNED NOT NULL AUTO_INCREMENT,
  `call_id`    INT(11) UNSIGNED NOT NULL,
  `user_id`    INT(10) UNSIGNED NOT NULL,
  `joined_at`  DATETIME         NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`id`),
  KEY `idx_call` (`call_id`),
  KEY `idx_user` (`user_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
