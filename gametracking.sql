CREATE TABLE IF NOT EXISTS `DepotVersions` (
  `ChangeID` INT(10) UNSIGNED NOT NULL,
  `AppID` INT(10) UNSIGNED NOT NULL,
  `DepotID` INT(10) UNSIGNED NOT NULL,
  `ManifestID` BIGINT(20) UNSIGNED NOT NULL,

  UNIQUE (`ChangeID`, `DepotID`)
);

CREATE TABLE IF NOT EXISTS `LocalConfig` (
  `Key` VARCHAR(256) NOT NULL,
  /* does this need to be mediumtext? */
  `Value` MEDIUMTEXT NOT NULL,

  PRIMARY KEY (`Key`)
);
