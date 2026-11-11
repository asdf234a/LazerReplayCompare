// The analyser's dan estimate for one chart, as its card shows it with the default settings:
// runAnalysisPipeline, then what js/app/analysis.js does after it (SunnyWindow's LN part,
// Companella for Mixed / Companella, the 18.5 cap, VIBRO). Only the analyser's own functions
// do the maths; this file only strings them together the way analysis.js does.
import { runAnalysisPipeline } from "../js/pipeline/runAnalysisPipeline.js";
import { applyCompanellaToMixedResult } from "../js/estimator/mixedEstimator.js";
import { classifyCompanellaDifficulty } from "../js/estimator/companellaEstimator.js";
import { analyzeEtternaFromText, DEFAULT_SCORE_GOAL } from "../js/ett/index.js";
import { detectVibro, detectVibroFromMetadata } from "../js/patterns/chartVibro.js";
import { APP_CONFIG } from "../config.js";

const D = APP_CONFIG.defaults;
const VIBRO_MSD_VERSION = "0.72.3";

export const DEFAULTS = {
    estimatorAlgorithm: D.estimatorAlgorithm,
    etternaVersion: D.etternaVersion,
    companellaEtternaVersion: D.companellaEtternaVersion,
    azusaSunnyReferenceHo: D.azusaSunnyReferenceHo,
    forceSunnyWindow: D.forceSunnyWindow,
    extendedEstimationRange: D.extendedEstimationRange,
    enableAlwaysShowLNDifficulty: D.enableAlwaysShowLNDifficulty,
    vibroDetection: D.vibroDetection,
};

export async function estimate(rawText, settings = {}) {
    const s = { ...DEFAULTS, ...settings };
    const speedRate = Number(settings.speedRate) || 1;
    const classicMod = settings.classicMod === true;
    // lazer's Hold Off / Invert: "HO" / "IN", as the analyser's mod setting
    const cvtFlag = settings.cvtFlag ?? null;
    const estimatorAlgorithm = s.estimatorAlgorithm;
    const needsCompanella = estimatorAlgorithm === "Companella" || estimatorAlgorithm === "Mixed";
    const ettOptions = (etternaVersion) => ({ musicRate: speedRate, scoreGoal: DEFAULT_SCORE_GOAL, cvtFlag, etternaVersion });

    const pipelineResult = await runAnalysisPipeline({
        rawText,
        estimatorAlgorithm,
        options: {
            speedRate,
            odFlag: settings.odFlag ?? null,
            cvtFlag,
            withGraph: false,
            extendedEstimationRange: s.extendedEstimationRange,
            enableAlwaysShowLNDifficulty: s.enableAlwaysShowLNDifficulty,
            forceSunnyReferenceHo: s.azusaSunnyReferenceHo,
            forceSunnyWindow: s.forceSunnyWindow,
            enableAnalyzeLN: false,
            display6kLevel: false,
            withPattern: false,
            withEtterna: needsCompanella || s.vibroDetection,
            withInterlude: needsCompanella,
            withPpMetrics: false,
            classicMod,
            etternaVersion: s.etternaVersion,
            companellaEtternaVersion: s.companellaEtternaVersion,
        },
    });

    const rework = pipelineResult.rework;
    let algorithm = pipelineResult.actualEstimatorAlgorithm;
    let estDiff = rework.estDiff;
    let numeric = rework.numericDifficulty;
    let pendingCompanella = estimatorAlgorithm === "Companella" && Number(rework.columnCount) === 4;
    let mixedPlan = estimatorAlgorithm === "Mixed" ? (rework.mixedCompanellaPlan || null) : null;

    // SunnyWindow replaces the LN part
    let lnStar = null;
    const window = pipelineResult.sunnyWindow;
    if (window) {
        const lnPart = window.estDiff.split("||").map((p) => p.trim()).filter((p) => p.length > 0)[1];
        if (lnPart) {
            estDiff = estDiff.split("||").map((p) => p.trim()).filter((p) => p.length > 0)[0] + " || " + lnPart;
            if (mixedPlan) {
                mixedPlan.lnDifficulty = lnPart;
                mixedPlan.lnRatio = 4e65;
            }
        }
        lnStar = window.lnStar;
    }

    // VIBRO: a keyword in the title / difficulty, or (over ★5) MSD's JackSpeed close to Overall
    const ettResult = pipelineResult.ettResult;
    let vibro = s.vibroDetection ? detectVibroFromMetadata(pipelineResult.parsedSummary.metadata) : false;
    if (s.vibroDetection && pipelineResult.vibro.eligible && ettResult) {
        let values = ettResult.values;
        if (Number(ettResult.keycount) === 4 && ettResult.etternaVersion !== VIBRO_MSD_VERSION) {
            try {
                values = (await analyzeEtternaFromText(rawText, ettOptions(VIBRO_MSD_VERSION)))?.values ?? values;
            } catch {
                // keep the main values
            }
        }
        vibro = vibro || detectVibro(values, APP_CONFIG.etterna.vibroJackspeedRatioThreshold);
    }

    // Companella (4K): ONNX on MSD + Interlude SR + Sunny SR
    if (Number(rework.columnCount) === 4 && (pendingCompanella || mixedPlan)) {
        let msd = ettResult?.values;
        const companellaVersion = String(s.companellaEtternaVersion || s.etternaVersion).trim() || s.etternaVersion;
        if (s.etternaVersion !== companellaVersion && pipelineResult.companellaEttResult) msd = pipelineResult.companellaEttResult.values;
        try {
            const companella = await classifyCompanellaDifficulty({
                msdValues: msd,
                interludeStar: pipelineResult.interludeStar,
                sunnyStar: Number(rework.star),
            });
            if (pendingCompanella) {
                estDiff = companella.estDiff;
                numeric = companella.numericDifficulty;
            }
            if (mixedPlan) {
                const after = applyCompanellaToMixedResult({ estDiff, numericDifficulty: numeric, numericDifficultyHint: null, mixedCompanellaPlan: mixedPlan }, companella);
                estDiff = after.estDiff;
                numeric = after.numericDifficulty;
                if (after.companellaCapsule) algorithm = after.companellaCapsule;
            }
        } catch (error) {
            if (pendingCompanella) algorithm = "Sunny";
        }
    }

    // What the card shows
    let label = estDiff;
    if (Number.isFinite(numeric) && numeric >= 18.5) {
        const parts = (estDiff || "").split("||");
        parts[0] = "> Cloverwisp Theta high";
        label = estDiff ? parts.join("||") : "> Cloverwisp Theta high";
    }
    const parts = String(label ?? "").split("||").map((p) => p.trim()).filter((p) => p.length > 0);
    return {
        star: Number(rework.star),
        lnRatio: Number(rework.lnRatio),
        columns: Number(rework.columnCount),
        rc: parts[0] ?? null,
        ln: parts[1] ?? null,
        numeric: Number.isFinite(numeric) && numeric < 18.5 ? numeric : null,
        algorithm,
        vibro,
        lnStar: Number.isFinite(Number(lnStar)) ? Number(lnStar) : null,
    };
}
