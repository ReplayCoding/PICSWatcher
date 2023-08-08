SET time_zone = "+00:00";

CREATE TABLE IF NOT EXISTS `DepotVersions` (
  `ChangeID` INT(10) UNSIGNED NOT NULL,
  `AppID` INT(10) UNSIGNED NOT NULL,
  `DepotID` INT(10) UNSIGNED NOT NULL,
  `ManifestID` BIGINT(20) UNSIGNED NOT NULL,

  UNIQUE (`ChangeID`, `DepotID`)
);

CREATE TABLE IF NOT EXISTS `BuildInfo` (
  `ChangeID` INT(10) UNSIGNED NOT NULL,
  `Branch` varchar(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `BuildID` INT(10) UNSIGNED NOT NULL,
  `TimeUpdated` DATETIME NOT NULL,

  UNIQUE (`ChangeID`)
);

CREATE TABLE IF NOT EXISTS `DepotKeys` (
  `DepotID` INT(10) UNSIGNED NOT NULL,
  `Key` VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,

  UNIQUE (`DepotID`)
);

CREATE TABLE IF NOT EXISTS `LocalConfig` (
  `Key` VARCHAR(256) NOT NULL,
  /* does this need to be mediumtext? */
  `Value` MEDIUMTEXT NOT NULL,

  PRIMARY KEY (`Key`)
);
