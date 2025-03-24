import { DependencyContainer } from "tsyringe";
import { ILogger } from "@spt/models/spt/utils/Ilogger";
import { IPostDBLoadMod } from "@spt/models/external/IPostDBLoadMod";
import { DatabaseServer } from "@spt/servers/DatabaseServer";
import { ItemHelper } from "@spt/helpers/ItemHelper";
import { BaseClasses } from "@spt/models/enums/BaseClasses";

import { existsSync } from "node:fs";
import { readFileSync } from "node:fs";
import { jsonc } from "jsonc";
import path from "path";

class LessRestrictingHeadwear implements IPostDBLoadMod
{
	public mod: string;
    	public modShortName: string;
	private logger: ILogger;
	private config: any;
	

	constructor() {
        this.mod = "MusicManiac - Less Restricting Headwear";
        this.modShortName = "LessRestrictingHeadwear";
	}

	public postDBLoad(container: DependencyContainer): void 
	{
		this.logger = container.resolve<ILogger>("WinstonLogger");
		const db = container.resolve<DatabaseServer>("DatabaseServer");
		const tables = db.getTables();    
		const itemDB = tables.templates.items;
		const itemHelper = container.resolve<ItemHelper>("ItemHelper");
		
		const configPath = path.resolve(__dirname, "../config.jsonc");
		if(!existsSync(configPath)) {
			this.logger.error(`${this.modShortName} Config file not found at ${configPath}`);
			return;
		}
		
		try
		{
			this.config = jsonc.parse(readFileSync(configPath, "utf8"));
			if(!this.config) {
				this.logger.error(`${this.mod} Failed to parse config file or config is empty`);
				return;
			}
		} catch (e) {
			this.logger.error(`${this.mod} Error parsing config file: ${e instanceof Error ? e.message : String(e)}`);
			return;
		}
		for (let item in itemDB) {
			if (itemDB[item]._type !== "Node") {
				const itemId = itemDB[item]._id
				if (itemHelper.isOfBaseclass(itemId, BaseClasses.HEADWEAR)) {
					if (this.config.debug) {
						this.logger.info(`[${this.modShortName}] adjusting item ${itemDB[item]._name} (id ${itemId} ) to match config values`);
					}
					itemDB[item]._props.BlocksHeadwear = this.config.Headwear.removeBlocksHeadwear ? false : itemDB[item]._props.BlocksHeadwear;
					itemDB[item]._props.BlocksEarpiece = this.config.Headwear.removeBlocksEarpiece ? false : itemDB[item]._props.BlocksEarpiece;
					itemDB[item]._props.BlocksFaceCover = this.config.Headwear.removeBlocksFaceCover ? false : itemDB[item]._props.BlocksFaceCover;
					itemDB[item]._props.BlocksEyewear = this.config.Headwear.removeBlocksEyewear ? false : itemDB[item]._props.BlocksEyewear;
					itemDB[item]._props.ConflictingItems = this.config.Headwear.clearConflictingItems ? [] : itemDB[item]._props.ConflictingItems;
				} else if (itemHelper.isOfBaseclass(itemId, BaseClasses.HEADPHONES)) {
					if (this.config.debug) {
						this.logger.info(`[${this.modShortName}] adjusting item ${itemDB[item]._name} (id ${itemId} ) to match config values`);
					}
					itemDB[item]._props.BlocksHeadwear = this.config.Earpiece.removeBlocksHeadwear ? false : itemDB[item]._props.BlocksHeadwear;
					itemDB[item]._props.BlocksEarpiece = this.config.Earpiece.removeBlocksEarpiece ? false : itemDB[item]._props.BlocksEarpiece;
					itemDB[item]._props.BlocksFaceCover = this.config.Earpiece.removeBlocksFaceCover ? false : itemDB[item]._props.BlocksFaceCover;
					itemDB[item]._props.BlocksEyewear = this.config.Earpiece.removeBlocksEyewear ? false : itemDB[item]._props.BlocksEyewear;
					itemDB[item]._props.ConflictingItems = this.config.Earpiece.clearConflictingItems ? [] : itemDB[item]._props.ConflictingItems;
				} else if (itemHelper.isOfBaseclass(itemId, BaseClasses.FACECOVER)) {
					if (this.config.debug) {
						this.logger.info(`[${this.modShortName}] adjusting item ${itemDB[item]._name} (id ${itemId} ) to match config values`);
					}
					itemDB[item]._props.BlocksHeadwear = this.config.FaceCover.removeBlocksHeadwear ? false : itemDB[item]._props.BlocksHeadwear;
					itemDB[item]._props.BlocksEarpiece = this.config.FaceCover.removeBlocksEarpiece ? false : itemDB[item]._props.BlocksEarpiece;
					itemDB[item]._props.BlocksFaceCover = this.config.FaceCover.removeBlocksFaceCover ? false : itemDB[item]._props.BlocksFaceCover;
					itemDB[item]._props.BlocksEyewear = this.config.FaceCover.removeBlocksEyewear ? false : itemDB[item]._props.BlocksEyewear;
					itemDB[item]._props.ConflictingItems = this.config.FaceCover.clearConflictingItems ? [] : itemDB[item]._props.ConflictingItems;
				} else if (itemHelper.isOfBaseclass(itemId, BaseClasses.VISORS)) {
					if (this.config.debug) {
						this.logger.info(`[${this.modShortName}] adjusting item ${itemDB[item]._name} (id ${itemId} ) to match config values`);
					}
					itemDB[item]._props.BlocksHeadwear = this.config.Eyewear.removeBlocksHeadwear ? false : itemDB[item]._props.BlocksHeadwear;
					itemDB[item]._props.BlocksEarpiece = this.config.Eyewear.removeBlocksEarpiece ? false : itemDB[item]._props.BlocksEarpiece;
					itemDB[item]._props.BlocksFaceCover = this.config.Eyewear.removeBlocksFaceCover ? false : itemDB[item]._props.BlocksFaceCover;
					itemDB[item]._props.BlocksEyewear = this.config.Eyewear.removeBlocksEyewear ? false : itemDB[item]._props.BlocksEyewear;
					itemDB[item]._props.ConflictingItems = this.config.Eyewear.clearConflictingItems ? [] : itemDB[item]._props.ConflictingItems;
				} else if (this.config.faceShields.includes(itemId)) {
					if (this.config.debug) {
						this.logger.info(`[${this.modShortName}] adjusting item ${itemDB[item]._name} (id ${itemId} ) to match config values`);
					}
					itemDB[item]._props.BlocksHeadwear = this.config.FaceShields.removeBlocksHeadwear ? false : itemDB[item]._props.BlocksHeadwear;
					itemDB[item]._props.BlocksEarpiece = this.config.FaceShields.removeBlocksEarpiece ? false : itemDB[item]._props.BlocksEarpiece;
					itemDB[item]._props.BlocksFaceCover = this.config.FaceShields.removeBlocksFaceCover ? false : itemDB[item]._props.BlocksFaceCover;
					itemDB[item]._props.BlocksEyewear = this.config.FaceShields.removeBlocksEyewear ? false : itemDB[item]._props.BlocksEyewear;
					itemDB[item]._props.ConflictingItems = this.config.FaceShields.clearConflictingItems ? [] : itemDB[item]._props.ConflictingItems;
				}
			}
		}
		this.logger.info(`[${this.modShortName}] ${this.mod} Loaded`);
	}
}

module.exports = { mod: new LessRestrictingHeadwear() }
