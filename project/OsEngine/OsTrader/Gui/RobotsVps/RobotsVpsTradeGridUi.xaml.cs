using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using OsEngine.Entity;
using OsEngine.Language;
using OsEngine.MCP.Client;
using OsEngine.OsTrader.Grids;
using Forms = System.Windows.Forms;
namespace OsEngine.OsTrader.Gui.RobotsVps
{
    // Layout/localization copied from TradeGridUi. All engine operations execute on the VPS.
    public partial class RobotsVpsTradeGridUi : Window
    {
        private readonly RemoteMcpClient _client;
        private readonly string _botId, _tab;
        private int? _number;
        private bool _loading = true, _busy, _closed;
        private string _regime = "Off";
        private readonly List<Field> _viewFields = new List<Field>();
        private readonly List<Field> _fields = new List<Field>();
        private readonly Forms.DataGridView _lines;
        private readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        private sealed class Field { public Control Control; public string Key, Type, Section; }
        public RobotsVpsTradeGridUi(RemoteMcpClient client, string botId, string tab, int? number)
        {
            InitializeComponent();
            OsEngine.Layout.StickyBorders.Listen(this);
            LabelNonTradePeriod1IsActive.Visibility = Visibility.Hidden;
            LabelNonTradePeriod2IsActive.Visibility = Visibility.Hidden;
            _client = client; _botId = botId; _tab = tab; _number = number;
            Title = OsLocalization.Trader.Label444 + " # " + tab + " # " + number;
            ComboBoxGridType.Items.Add(TradeGridPrimeType.MarketMaking.ToString());
            ComboBoxGridType.Items.Add(TradeGridPrimeType.OpenPosition.ToString());
            ComboBoxRegime.Items.Add(TradeGridRegime.Off.ToString());
            ComboBoxRegime.Items.Add(TradeGridRegime.OffAndCancelOrders.ToString());
            ComboBoxRegime.Items.Add(TradeGridRegime.On.ToString());
            ComboBoxRegime.Items.Add(TradeGridRegime.CloseOnly.ToString());
            ComboBoxRegime.Items.Add(TradeGridRegime.CloseForced.ToString());
            ComboBoxRegimeLogicEntry.Items.Add(TradeGridLogicEntryRegime.OnTrade.ToString());
            ComboBoxRegimeLogicEntry.Items.Add(TradeGridLogicEntryRegime.OncePerSecond.ToString());
            ComboBoxAutoClearJournal.Items.Add("True");
            ComboBoxAutoClearJournal.Items.Add("False");
            ComboBoxCheckMicroVolumes.Items.Add("True");
            ComboBoxCheckMicroVolumes.Items.Add("False");
            ComboBoxNonTradePeriod1Regime.Items.Add(TradeGridRegime.Off.ToString());
            ComboBoxNonTradePeriod1Regime.Items.Add(TradeGridRegime.OffAndCancelOrders.ToString());
            ComboBoxNonTradePeriod1Regime.Items.Add(TradeGridRegime.CloseOnly.ToString());
            ComboBoxNonTradePeriod1Regime.Items.Add(TradeGridRegime.CloseForced.ToString());
            ComboBoxNonTradePeriod2Regime.Items.Add(TradeGridRegime.Off.ToString());
            ComboBoxNonTradePeriod2Regime.Items.Add(TradeGridRegime.OffAndCancelOrders.ToString());
            ComboBoxNonTradePeriod2Regime.Items.Add(TradeGridRegime.CloseOnly.ToString());
            ComboBoxNonTradePeriod2Regime.Items.Add(TradeGridRegime.CloseForced.ToString());
            ComboBoxOpenOrdersMakerOnly.Items.Add(true.ToString());
            ComboBoxOpenOrdersMakerOnly.Items.Add(false.ToString());
            ComboBoxCloseForcedRegimeOrderType.Items.Add(OrderPriceType.Market.ToString());
            ComboBoxCloseForcedRegimeOrderType.Items.Add(OrderPriceType.Limit.ToString());
            ComboBoxStopGridByMoveUpReaction.Items.Add(TradeGridRegime.CloseForced.ToString());
            ComboBoxStopGridByMoveUpReaction.Items.Add(TradeGridRegime.CloseOnly.ToString());
            ComboBoxStopGridByMoveUpReaction.Items.Add(TradeGridRegime.OffAndCancelOrders.ToString());
            ComboBoxStopGridByMoveUpReaction.Items.Add(TradeGridRegime.Off.ToString());
            ComboBoxStopGridByMoveDownReaction.Items.Add(TradeGridRegime.CloseForced.ToString());
            ComboBoxStopGridByMoveDownReaction.Items.Add(TradeGridRegime.CloseOnly.ToString());
            ComboBoxStopGridByMoveDownReaction.Items.Add(TradeGridRegime.OffAndCancelOrders.ToString());
            ComboBoxStopGridByMoveDownReaction.Items.Add(TradeGridRegime.Off.ToString());
            ComboBoxStopGridByPositionsCountReaction.Items.Add(TradeGridRegime.CloseForced.ToString());
            ComboBoxStopGridByPositionsCountReaction.Items.Add(TradeGridRegime.CloseOnly.ToString());
            ComboBoxStopGridByPositionsCountReaction.Items.Add(TradeGridRegime.OffAndCancelOrders.ToString());
            ComboBoxStopGridByPositionsCountReaction.Items.Add(TradeGridRegime.Off.ToString());
            ComboBoxStopGridByLifeTimeReaction.Items.Add(TradeGridRegime.CloseForced.ToString());
            ComboBoxStopGridByLifeTimeReaction.Items.Add(TradeGridRegime.CloseOnly.ToString());
            ComboBoxStopGridByLifeTimeReaction.Items.Add(TradeGridRegime.OffAndCancelOrders.ToString());
            ComboBoxStopGridByLifeTimeReaction.Items.Add(TradeGridRegime.Off.ToString());
            ComboBoxStopGridByTimeOfDayReaction.Items.Add(TradeGridRegime.CloseForced.ToString());
            ComboBoxStopGridByTimeOfDayReaction.Items.Add(TradeGridRegime.CloseOnly.ToString());
            ComboBoxStopGridByTimeOfDayReaction.Items.Add(TradeGridRegime.OffAndCancelOrders.ToString());
            ComboBoxStopGridByTimeOfDayReaction.Items.Add(TradeGridRegime.Off.ToString());
            ComboBoxGridSide.Items.Add(Side.Buy.ToString());
            ComboBoxGridSide.Items.Add(Side.Sell.ToString());
            ComboBoxTypeStep.Items.Add(TradeGridValueType.Percent.ToString());
            ComboBoxTypeStep.Items.Add(TradeGridValueType.Absolute.ToString());
            ComboBoxTypeProfit.Items.Add(TradeGridValueType.Percent.ToString());
            ComboBoxTypeProfit.Items.Add(TradeGridValueType.Absolute.ToString());
            ComboBoxTypeVolume.Items.Add(TradeGridVolumeType.Contracts.ToString());
            ComboBoxTypeVolume.Items.Add(TradeGridVolumeType.ContractCurrency.ToString());
            ComboBoxTypeVolume.Items.Add(TradeGridVolumeType.DepositPercent.ToString());
            ComboBoxProfitRegime.Items.Add(TradeGridRegime.Off.ToString());
            ComboBoxProfitRegime.Items.Add(TradeGridRegime.On.ToString());
            ComboBoxProfitValueType.Items.Add(TradeGridValueType.Percent.ToString());
            ComboBoxProfitValueType.Items.Add(TradeGridValueType.Absolute.ToString());
            ComboBoxStopRegime.Items.Add(TradeGridRegime.Off.ToString());
            ComboBoxStopRegime.Items.Add(TradeGridRegime.On.ToString());
            ComboBoxStopValueType.Items.Add(TradeGridValueType.Percent.ToString());
            ComboBoxStopValueType.Items.Add(TradeGridValueType.Absolute.ToString());
            ComboBoxTrailStopRegime.Items.Add(TradeGridRegime.Off.ToString());
            ComboBoxTrailStopRegime.Items.Add(TradeGridRegime.On.ToString());
            ComboBoxTrailStopValueType.Items.Add(TradeGridValueType.Percent.ToString());
            ComboBoxTrailStopValueType.Items.Add(TradeGridValueType.Absolute.ToString());
            ComboBoxAutoStartRegime.Items.Add(TradeGridAutoStartRegime.Off.ToString());
            ComboBoxAutoStartRegime.Items.Add(TradeGridAutoStartRegime.LowerOrEqual.ToString());
            ComboBoxAutoStartRegime.Items.Add(TradeGridAutoStartRegime.HigherOrEqual.ToString());
            ComboBoxRebuildGridRegime.Items.Add(GridAutoStartShiftFirstPriceRegime.Off.ToString());
            ComboBoxRebuildGridRegime.Items.Add(GridAutoStartShiftFirstPriceRegime.On_FullRebuild.ToString());
            ComboBoxRebuildGridRegime.Items.Add(GridAutoStartShiftFirstPriceRegime.On_ShiftOnNewPrice.ToString());
            LabelGridType.Content = OsLocalization.Trader.Label445;
            LabelRegime.Content = OsLocalization.Trader.Label448;
            LabelRegimeLogicEntry.Content = OsLocalization.Trader.Label449;
            LabelAutoClearJournal.Content = OsLocalization.Trader.Label451;
            LabelMaxClosePositionsInJournal.Content = OsLocalization.Trader.Label452;
            ButtonLoad.Content = OsLocalization.Trader.Label453;
            ButtonSave.Content = OsLocalization.Trader.Label454;
            ButtonStart.Content = OsLocalization.Trader.Label455;
            ButtonStop.Content = OsLocalization.Trader.Label456;
            ButtonClose.Content = OsLocalization.Trader.Label457;
            LabelMaxOrdersInMarket.Content = OsLocalization.Trader.Label488;
            LabelMaxOpenOrdersInMarket.Content = OsLocalization.Trader.Label508;
            LabelMaxCloseOrdersInMarket.Content = OsLocalization.Trader.Label509;
            LabelDelayInReal.Content = OsLocalization.Trader.Label569;
            LabelCheckMicroVolumes.Content = OsLocalization.Trader.Label572;
            LabelMaxDistanceToOrdersPercent.Content = OsLocalization.Trader.Label581;
            TabItemBaseSettings.Header = OsLocalization.Trader.Label458;
            TabItemGridCreation.Header = OsLocalization.Trader.Label459;
            TabItemNonTradePeriods.Header = OsLocalization.Trader.Label633;
            TabItemStopTrading.Header = OsLocalization.Trader.Label463;
            TabItemStopAndProfit.Header = OsLocalization.Trader.Label464;
            TabItemGridLinesTable.Header = OsLocalization.Trader.Label465;
            TabItemAutoStart.Header = OsLocalization.Trader.Label472;
            TabItemError.Header = OsLocalization.Trader.Label537;
            TabItemTrailingUp.Header = OsLocalization.Trader.Label544;
            LabelNoTradePeriod1Regime.Content = OsLocalization.Trader.Label506 + " #1";
            ButtonSetNonTradePeriods.Content = OsLocalization.Trader.Label632 + " #1";
            LabelNoTradePeriod2Regime.Content = OsLocalization.Trader.Label506 + " #2";
            ButtonSetNonTradePeriods2.Content = OsLocalization.Trader.Label632 + " #2";
            LabelOpenOrdersMakerOnly.Content = OsLocalization.Trader.Label635;
            LabelNonTradePeriod1IsActive.Content = OsLocalization.Trader.Label638;
            LabelNonTradePeriod2IsActive.Content = OsLocalization.Trader.Label638;
            LabelServerTime.Content = OsLocalization.Trader.Label672;
            LabelOpenCloseForcedRegimeOrderType.Content = OsLocalization.Trader.Label673;
            CheckBoxStopGridByMoveUpIsOn.Content = OsLocalization.Trader.Label481;
            LabelStopGridByMoveUpValuePercentReaction.Content = OsLocalization.Trader.Label484;
            CheckBoxStopGridByMoveDownIsOn.Content = OsLocalization.Trader.Label482;
            LabelStopGridByMoveDownValuePercentReaction.Content = OsLocalization.Trader.Label484;
            CheckBoxStopGridByPositionsCountIsOn.Content = OsLocalization.Trader.Label483;
            LabelStopGridByPositionsCountIsOnReaction.Content = OsLocalization.Trader.Label484;
            CheckBoxStopGridByLifeTimeIsOn.Content = OsLocalization.Trader.Label525;
            LabelStopGridByLifeTimeOnReaction.Content = OsLocalization.Trader.Label484;
            CheckBoxStopGridByTimeOfDayIsOn.Content = OsLocalization.Trader.Label526;
            LabelStopGridByTimeOfDayReaction.Content = OsLocalization.Trader.Label484;
            LabelStopGridByTimeOfDayHour.Content = OsLocalization.Trader.Label527 + ":";
            LabelStopGridByTimeOfDayMinute.Content = OsLocalization.Trader.Label528 + ":";
            LabelStopGridByTimeOfDaySecond.Content = OsLocalization.Trader.Label529 + ":";
            LabelGridSide.Content = OsLocalization.Trader.Label485;
            LabelFirstPrice.Content = OsLocalization.Trader.Label486;
            LabelLinesCount.Content = OsLocalization.Trader.Label487;
            LabelStep.Content = OsLocalization.Trader.Label489;
            LabelProfit.Content = OsLocalization.Trader.Label490;
            LabelVolume.Content = OsLocalization.Trader.Label491;
            LabelAsset.Content = OsLocalization.Trader.Label492;
            ButtonCreateGrid.Content = OsLocalization.Trader.Label493;
            ButtonDeleteGrid.Content = OsLocalization.Trader.Label494;
            ButtonNewLevel.Content = OsLocalization.Trader.Label495;
            ButtonRemoveSelected.Content = OsLocalization.Trader.Label496;
            LabelSelectOffToUse.Content = OsLocalization.Trader.Label530;
            LabelProfitRegime.Content = OsLocalization.Trader.Label497;
            LabelProfitValueType.Content = OsLocalization.Trader.Label498;
            LabelProfitValue.Content = OsLocalization.Trader.Label499;
            CheckBoxStopByProfit.Content = OsLocalization.Trader.Label644;
            LabelStopRegime.Content = OsLocalization.Trader.Label500;
            LabelStopValueType.Content = OsLocalization.Trader.Label498;
            LabelStopValue.Content = OsLocalization.Trader.Label499;
            LabelTrailStopRegime.Content = OsLocalization.Trader.Label531;
            LabelTrailStopValueType.Content = OsLocalization.Trader.Label498;
            LabelTrailStopValue.Content = OsLocalization.Trader.Label499;
            LabelMiddleEntryPrice.Content = OsLocalization.Trader.Label532;
            LabelAutoStartRegime.Content = OsLocalization.Trader.Label504;
            LabelAutoStartPrice.Content = OsLocalization.Trader.Label505;
            LabelRebuildGridRegime.Content = OsLocalization.Trader.Label535;
            LabelShiftFirstPrice.Content = OsLocalization.Trader.Label536;
            CheckBoxStartGridByTimeOfDayIsOn.Content = OsLocalization.Trader.Label634;
            LabelStartGridByTimeOfDayHour.Content = OsLocalization.Trader.Label527 + ":";
            LabelStartGridByTimeOfDayMinute.Content = OsLocalization.Trader.Label528 + ":";
            LabelStartGridByTimeOfDaySecond.Content = OsLocalization.Trader.Label529 + ":";
            CheckBoxSingleActivationMode.Content = OsLocalization.Trader.Label636;
            CheckBoxFailOpenOrdersReactionIsOn.Content = OsLocalization.Trader.Label538;
            LabelFailOpenOrdersCountToReaction.Content = OsLocalization.Trader.Label539;
            LabelFailOpenOrdersCountFact.Content = OsLocalization.Trader.Label540;
            CheckBoxFailCancelOrdersReactionIsOn.Content = OsLocalization.Trader.Label541;
            LabelFailCancelOrdersCountToReaction.Content = OsLocalization.Trader.Label542;
            LabelFailCancelOrdersCountFact.Content = OsLocalization.Trader.Label543;
            CheckBoxWaitOnStartConnectorIsOn.Content = OsLocalization.Trader.Label582;
            LabelWaitSecondsOnStartConnector.Content = OsLocalization.Trader.Label583;
            CheckBoxReduceOrdersCountInMarketOnNoFundsError.Content = OsLocalization.Trader.Label671;
            CheckBoxTrailingUpIsOn.Content = OsLocalization.Trader.Label545;
            LabelTrailingUpStep.Content = OsLocalization.Trader.Label549;
            LabelTrailingUpLimit.Content = OsLocalization.Trader.Label547;
            CheckBoxTrailingUpCanMoveExitOrder.Content = OsLocalization.Trader.Label571;
            CheckBoxTrailingDownIsOn.Content = OsLocalization.Trader.Label546;
            LabelTrailingDownStep.Content = OsLocalization.Trader.Label549;
            LabelTrailingDownLimit.Content = OsLocalization.Trader.Label547;
            CheckBoxTrailingDownCanMoveExitOrder.Content = OsLocalization.Trader.Label571;
            Bind(ComboBoxGridType, "grid_type", "string", "");
            Bind(ComboBoxRegime, "regime", "string", "");
            Bind(ComboBoxAutoClearJournal, "auto_clear_journal_is_on", "boolean", "");
            Bind(TextBoxMaxClosePositionsInJournal, "max_close_positions_in_journal", "integer", "");
            Bind(TextBoxMaxOpenOrdersInMarket, "max_open_orders_in_market", "integer", "");
            Bind(TextBoxMaxCloseOrdersInMarket, "max_close_orders_in_market", "integer", "");
            Bind(TextBoxDelayInReal, "delay_in_real", "integer", "");
            Bind(ComboBoxCheckMicroVolumes, "check_micro_volumes", "boolean", "");
            Bind(TextBoxMaxDistanceToOrdersPercent, "max_distance_to_orders_percent", "number", "");
            Bind(ComboBoxOpenOrdersMakerOnly, "open_orders_maker_only", "boolean", "");
            Bind(ComboBoxCloseForcedRegimeOrderType, "close_forced_regime_order_type", "string", "");
            Bind(ComboBoxGridSide, "grid_side", "string", "creator");
            Bind(TextBoxFirstPrice, "first_price", "number", "creator");
            Bind(TextBoxLineCountStart, "line_count_start", "integer", "creator");
            Bind(ComboBoxTypeStep, "type_step", "string", "creator");
            Bind(TextBoxLineStep, "line_step", "number", "creator");
            Bind(TextBoxStepMultiplicator, "step_multiplicator", "number", "creator");
            Bind(ComboBoxTypeProfit, "type_profit", "string", "creator");
            Bind(TextBoxProfitStep, "profit_step", "number", "creator");
            Bind(TextBoxProfitMultiplicator, "profit_multiplicator", "number", "creator");
            Bind(ComboBoxTypeVolume, "type_volume", "string", "creator");
            Bind(TextBoxStartVolume, "start_volume", "number", "creator");
            Bind(TextBoxMartingaleMultiplicator, "martingale_multiplicator", "number", "creator");
            Bind(TextBoxTradeAssetInPortfolio, "trade_asset_in_portfolio", "string", "creator");
            Bind(ComboBoxProfitRegime, "profit_regime", "string", "stop_and_profit");
            Bind(ComboBoxProfitValueType, "profit_value_type", "string", "stop_and_profit");
            Bind(TextBoxProfitValue, "profit_value", "number", "stop_and_profit");
            Bind(CheckBoxStopByProfit, "stop_trading_after_profit", "boolean", "stop_and_profit");
            Bind(ComboBoxStopRegime, "stop_regime", "string", "stop_and_profit");
            Bind(ComboBoxStopValueType, "stop_value_type", "string", "stop_and_profit");
            Bind(TextBoxStopValue, "stop_value", "number", "stop_and_profit");
            Bind(ComboBoxTrailStopRegime, "trail_stop_regime", "string", "stop_and_profit");
            Bind(ComboBoxTrailStopValueType, "trail_stop_value_type", "string", "stop_and_profit");
            Bind(TextBoxTrailStopValue, "trail_stop_value", "number", "stop_and_profit");
            Bind(CheckBoxTrailingUpIsOn, "trailing_up_is_on", "boolean", "trailing_up");
            Bind(TextBoxTrailingUpStep, "trailing_up_step", "number", "trailing_up");
            Bind(TextBoxTrailingUpLimit, "trailing_up_limit", "number", "trailing_up");
            Bind(CheckBoxTrailingUpCanMoveExitOrder, "trailing_up_can_move_exit_order", "boolean", "trailing_up");
            Bind(CheckBoxTrailingDownIsOn, "trailing_down_is_on", "boolean", "trailing_up");
            Bind(TextBoxTrailingDownStep, "trailing_down_step", "number", "trailing_up");
            Bind(TextBoxTrailingDownLimit, "trailing_down_limit", "number", "trailing_up");
            Bind(CheckBoxTrailingDownCanMoveExitOrder, "trailing_down_can_move_exit_order", "boolean", "trailing_up");
            Stub(ComboBoxRegimeLogicEntry);
            Stub(ButtonLoad);
            Stub(ButtonSave);
            ButtonBase.Click += (s,e) => { try { InteractiveInstructions.Grids.Link17.ShowLinkInBrowser(); } catch(Exception ex) { ShowError(ex); } };
            Stub(ButtonDeleteGrid);
            Stub(ButtonNewLevel);
            Stub(ButtonRemoveSelected);
            ButtonCreation.Click += (s,e) => { try { InteractiveInstructions.Grids.Link18.ShowLinkInBrowser(); } catch(Exception ex) { ShowError(ex); } };
            Stub(CheckBoxStopGridByMoveUpIsOn);
            Stub(TextBoxStopGridByMoveUpValuePercent);
            Stub(ComboBoxStopGridByMoveUpReaction);
            Stub(CheckBoxStopGridByMoveDownIsOn);
            Stub(TextBoxStopGridByMoveDownValuePercent);
            Stub(ComboBoxStopGridByMoveDownReaction);
            Stub(CheckBoxStopGridByPositionsCountIsOn);
            Stub(TextBoxStopGridByPositionsCountValue);
            Stub(ComboBoxStopGridByPositionsCountReaction);
            Stub(CheckBoxStopGridByLifeTimeIsOn);
            Stub(TextBoxStopGridByLifeTimeSecondsToLife);
            Stub(ComboBoxStopGridByLifeTimeReaction);
            Stub(CheckBoxStopGridByTimeOfDayIsOn);
            Stub(TextBoxStopGridByTimeOfDayHour);
            Stub(TextBoxStopGridByTimeOfDayMinute);
            Stub(TextBoxStopGridByTimeOfDaySecond);
            Stub(ComboBoxStopGridByTimeOfDayReaction);
            ButtonStopTrading.Click += (s,e) => { try { InteractiveInstructions.Grids.Link19.ShowLinkInBrowser(); } catch(Exception ex) { ShowError(ex); } };
            Stub(ComboBoxAutoStartRegime);
            Stub(TextBoxAutoStartPrice);
            Stub(ComboBoxRebuildGridRegime);
            Stub(TextBoxShiftFirstPrice);
            Stub(CheckBoxStartGridByTimeOfDayIsOn);
            Stub(TextBoxStartGridByTimeOfDayHour);
            Stub(TextBoxStartGridByTimeOfDayMinute);
            Stub(TextBoxStartGridByTimeOfDaySecond);
            Stub(CheckBoxSingleActivationMode);
            ButtonAutoStart.Click += (s,e) => { try { InteractiveInstructions.Grids.Link20.ShowLinkInBrowser(); } catch(Exception ex) { ShowError(ex); } };
            Stub(ButtonTrailUpInstruction);
            Stub(ComboBoxNonTradePeriod1Regime);
            Stub(ButtonSetNonTradePeriods);
            Stub(ComboBoxNonTradePeriod2Regime);
            Stub(ButtonSetNonTradePeriods2);
            Stub(TextBoxCurrentServerTime);
            Stub(ButtonPosts);
            Stub(TextBoxMiddleEntryPrice);
            ButtonStopAndProfit.Click += (s,e) => { try { InteractiveInstructions.Grids.Link21.ShowLinkInBrowser(); } catch(Exception ex) { ShowError(ex); } };
            Stub(CheckBoxFailOpenOrdersReactionIsOn);
            Stub(TextBoxFailOpenOrdersCountToReaction);
            Stub(TextBoxFailOpenOrdersCountFact);
            Stub(CheckBoxFailCancelOrdersReactionIsOn);
            Stub(TextBoxFailCancelOrdersCountToReaction);
            Stub(TextBoxFailCancelOrdersCountFact);
            Stub(CheckBoxWaitOnStartConnectorIsOn);
            Stub(TextBoxWaitSecondsOnStartConnector);
            Stub(CheckBoxReduceOrdersCountInMarketOnNoFundsError);
            ButtonError.Click += (s,e) => { try { InteractiveInstructions.Grids.Link22.ShowLinkInBrowser(); } catch(Exception ex) { ShowError(ex); } };
            View(ComboBoxRegimeLogicEntry, "prime", "RegimeLogicEntry");
            View(ComboBoxNonTradePeriod1Regime, "non_trade", "NonTradePeriod1Regime");
            View(ComboBoxNonTradePeriod2Regime, "non_trade", "NonTradePeriod2Regime");
            View(CheckBoxStopGridByMoveUpIsOn, "stop_by", "StopGridByMoveUpIsOn");
            View(TextBoxStopGridByMoveUpValuePercent, "stop_by", "StopGridByMoveUpValuePercent");
            View(ComboBoxStopGridByMoveUpReaction, "stop_by", "StopGridByMoveUpReaction");
            View(CheckBoxStopGridByMoveDownIsOn, "stop_by", "StopGridByMoveDownIsOn");
            View(TextBoxStopGridByMoveDownValuePercent, "stop_by", "StopGridByMoveDownValuePercent");
            View(ComboBoxStopGridByMoveDownReaction, "stop_by", "StopGridByMoveDownReaction");
            View(CheckBoxStopGridByPositionsCountIsOn, "stop_by", "StopGridByPositionsCountIsOn");
            View(TextBoxStopGridByPositionsCountValue, "stop_by", "StopGridByPositionsCountValue");
            View(ComboBoxStopGridByPositionsCountReaction, "stop_by", "StopGridByPositionsCountReaction");
            View(CheckBoxStopGridByLifeTimeIsOn, "stop_by", "StopGridByLifeTimeIsOn");
            View(TextBoxStopGridByLifeTimeSecondsToLife, "stop_by", "StopGridByLifeTimeSecondsToLife");
            View(ComboBoxStopGridByLifeTimeReaction, "stop_by", "StopGridByLifeTimeReaction");
            View(CheckBoxStopGridByTimeOfDayIsOn, "stop_by", "StopGridByTimeOfDayIsOn");
            View(TextBoxStopGridByTimeOfDayHour, "stop_by", "StopGridByTimeOfDayHour");
            View(TextBoxStopGridByTimeOfDayMinute, "stop_by", "StopGridByTimeOfDayMinute");
            View(TextBoxStopGridByTimeOfDaySecond, "stop_by", "StopGridByTimeOfDaySecond");
            View(ComboBoxStopGridByTimeOfDayReaction, "stop_by", "StopGridByTimeOfDayReaction");
            View(ComboBoxAutoStartRegime, "auto_start", "AutoStartRegime");
            View(TextBoxAutoStartPrice, "auto_start", "AutoStartPrice");
            View(ComboBoxRebuildGridRegime, "auto_start", "RebuildGridRegime");
            View(TextBoxShiftFirstPrice, "auto_start", "ShiftFirstPrice");
            View(CheckBoxStartGridByTimeOfDayIsOn, "auto_start", "StartGridByTimeOfDayIsOn");
            View(TextBoxStartGridByTimeOfDayHour, "auto_start", "StartGridByTimeOfDayHour");
            View(TextBoxStartGridByTimeOfDayMinute, "auto_start", "StartGridByTimeOfDayMinute");
            View(TextBoxStartGridByTimeOfDaySecond, "auto_start", "StartGridByTimeOfDaySecond");
            View(CheckBoxSingleActivationMode, "auto_start", "SingleActivationMode");
            View(CheckBoxFailOpenOrdersReactionIsOn, "errors", "FailOpenOrdersReactionIsOn");
            View(TextBoxFailOpenOrdersCountToReaction, "errors", "FailOpenOrdersCountToReaction");
            View(TextBoxFailOpenOrdersCountFact, "errors", "FailOpenOrdersCountFact");
            View(CheckBoxFailCancelOrdersReactionIsOn, "errors", "FailCancelOrdersReactionIsOn");
            View(TextBoxFailCancelOrdersCountToReaction, "errors", "FailCancelOrdersCountToReaction");
            View(TextBoxFailCancelOrdersCountFact, "errors", "FailCancelOrdersCountFact");
            View(CheckBoxWaitOnStartConnectorIsOn, "errors", "WaitOnStartConnectorIsOn");
            View(TextBoxWaitSecondsOnStartConnector, "errors", "WaitSecondsOnStartConnector");
            View(CheckBoxReduceOrdersCountInMarketOnNoFundsError, "errors", "ReduceOrdersCountInMarketOnNoFundsError");
            _lines = DataGridFactory.GetDataGridView(Forms.DataGridViewSelectionMode.FullRowSelect, Forms.DataGridViewAutoSizeRowsMode.AllCells);
            _lines.ReadOnly = true;
            string[] headers = { "#", OsLocalization.Trader.Label20, OsLocalization.Trader.Label400, OsLocalization.Trader.Label401, OsLocalization.Trader.Label491, OsLocalization.Trader.Label403, OsLocalization.Trader.Label485, "" };
            foreach (string header in headers)
                _lines.Columns.Add(new Forms.DataGridViewTextBoxColumn { HeaderText = header, AutoSizeMode = Forms.DataGridViewAutoSizeColumnMode.Fill });
            HostGridTable.Child = _lines;
            ButtonCreateGrid.Click += async (s,e) => await CreateAsync();
            ButtonStart.Click += async (s,e) => await SetRegimeAsync("On");
            ButtonStop.Click += async (s,e) => await SetRegimeAsync("Off");
            ButtonClose.Click += async (s,e) => await SetRegimeAsync("CloseForced");
            Loaded += async (s,e) => {
                try {
                    if (_number.HasValue) await ReloadAsync(true);
                    else LoadDefaults();
                    _loading = false; UpdateEnabled(); _timer.Start();
                } catch(Exception ex) { ShowError(ex); }
            };
            _timer.Tick += async (s,e) => { if (!_busy && _number.HasValue) {
                _busy = true;
                try { await ReloadAsync(false); } catch(Exception ex) { _timer.Stop(); ShowError(ex); }
                finally { _busy = false; UpdateEnabled(); }
            }};
            Closed += (s,e) => { _closed = true; _timer.Stop(); HostGridTable.Child = null; _lines.Dispose(); };
        }
        private void View(Control control, string section, string key)
        {
            _viewFields.Add(new Field { Control=control, Section=section, Key=key });
            control.ToolTip = "VPS: данные сервера; изменение этой настройки пока не подключено";
        }
        private void Stub(Control control)
        {
            control.IsEnabled = false;
            control.ToolTip = "VPS: эта функция ещё не подключена к серверу";
            ToolTipService.SetShowOnDisabled(control, true);
            if (control is TextBox text) text.Text = "";
            if (control is ComboBox combo) combo.SelectedIndex = -1;
            if (control is CheckBox check) check.IsChecked = null;
        }
        private void Bind(Control control, string key, string type, string section)
        {
            Field field = new Field { Control = control, Key = key, Type = type, Section = section };
            _fields.Add(field);
            if (control is TextBox text) text.LostKeyboardFocus += async (s,e) => await SaveFieldAsync(field);
            if (control is ComboBox combo) combo.SelectionChanged += async (s,e) => await SaveFieldAsync(field);
            if (control is CheckBox check) check.Click += async (s,e) => await SaveFieldAsync(field);
        }
        private void LoadDefaults()
        {
            TradeGridCreator creator = new TradeGridCreator();
            foreach (Field f in _fields)
            {
                if (f.Section != "creator") continue;
                foreach (System.Reflection.FieldInfo member in typeof(TradeGridCreator).GetFields())
                {
                    string key = System.Text.RegularExpressions.Regex.Replace(member.Name, "(?<!^)(?=[A-Z])", "_").ToLowerInvariant();
                    if (key == f.Key) SetValue(f, Convert.ToString(member.GetValue(creator), CultureInfo.CurrentCulture));
                }
            }
            ComboBoxGridType.SelectedItem = "MarketMaking";
            ComboBoxRegime.SelectedItem = "Off";
        }
        private void SetValue(Field field, string value)
        {
            if (field.Control is TextBox text) text.Text = value;
            else if (field.Control is ComboBox combo) combo.SelectedItem = value;
            else if (field.Control is CheckBox check) check.IsChecked = bool.TryParse(value, out bool b) && b;
        }
        private object GetValue(Field f)
        {
            string value = f.Control is TextBox text ? text.Text : f.Control is ComboBox combo ? combo.SelectedItem?.ToString() : ((CheckBox)f.Control).IsChecked.ToString();
            if (f.Type == "integer") return int.Parse(value, CultureInfo.CurrentCulture);
            if (f.Type == "number") return value.ToDecimal();
            if (f.Type == "boolean") return bool.Parse(value);
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Empty " + f.Key);
            return value;
        }
        private Dictionary<string, object> Args() => new Dictionary<string, object> { ["bot_id"] = _botId, ["tab_name"] = _tab, ["grid_number"] = _number };
        private async Task SaveFieldAsync(Field f)
        {
            if (_loading || _busy || !_number.HasValue || _closed) return;
            if (f.Key == "regime") { await SetRegimeAsync(GetValue(f).ToString()); return; }
            _busy = true; UpdateEnabled();
            try {
                Dictionary<string,object> args = Args(); args[f.Key] = GetValue(f);
                await _client.CallToolAsync("bot_grid_set_settings", args);
                await ReloadAsync(true);
            } catch(Exception ex) { ShowError(ex); try { await ReloadAsync(true); } catch(Exception reload) { ShowError(reload); } }
            finally { _busy = false; UpdateEnabled(); }
        }
        private async Task CreateAsync()
        {
            if (_busy || _number.HasValue) return;
            _busy = true; UpdateEnabled();
            try {
                Dictionary<string,object> args = Args(); args.Remove("grid_number");
                foreach(Field f in _fields) if(f.Section == "creator" || f.Key == "grid_type") args[f.Key] = GetValue(f);
                JsonElement created = await _client.CallToolAsync("bot_grid_create",args);
                _number = created.GetProperty("number").GetInt32();
                await ReloadAsync(true);
            } catch(Exception ex) { ShowError(ex); }
            finally { _busy = false; UpdateEnabled(); }
        }
        private async Task SetRegimeAsync(string regime)
        {
            if (_loading || _busy || !_number.HasValue) return;
            _busy = true; UpdateEnabled();
            try {
                Dictionary<string,object> args=Args(); args["regime"]=regime;
                await _client.CallToolAsync("bot_grid_set_regime",args);
                await ReloadAsync(true);
            } catch(Exception ex) { ShowError(ex); }
            finally { _busy=false; UpdateEnabled(); }
        }
        private async Task ReloadAsync(bool settings)
        {
            JsonElement data = await _client.CallToolAsync("bot_grid_get",Args());
            if (_closed) return;
            _loading = true;
            try {
                _regime=data.GetProperty("regime").GetString();
                ComboBoxRegime.SelectedItem = _regime;
                if(settings) foreach(Field f in _fields) {
                    JsonElement section=data;
                    if(f.Section!="" && !data.TryGetProperty(f.Section,out section)) continue;
                    if(section.TryGetProperty(f.Key,out JsonElement value))
                        SetValue(f, value.ValueKind==JsonValueKind.Number ? value.GetDecimal().ToString(CultureInfo.CurrentCulture) : value.ToString());
                }
                if (data.TryGetProperty("view_settings", out JsonElement view))
                    foreach(Field f in _viewFields)
                        if(view.TryGetProperty(f.Section,out JsonElement section) && section.TryGetProperty(f.Key,out JsonElement value))
                            SetValue(f,value.ToString());
                _lines.Rows.Clear(); int i=0;
                foreach(JsonElement line in data.GetProperty("lines").EnumerateArray()) {
                    Forms.DataGridViewRow row = new Forms.DataGridViewRow();
                    object[] values = { ++i, line.GetProperty("position_num").ToString(), line.GetProperty("price_enter").ToString(),
                        data.GetProperty("grid_type").GetString() == "OpenPosition" ? "_" : line.GetProperty("price_exit").ToString(),
                        line.GetProperty("volume").ToString(), line.TryGetProperty("open_volume", out JsonElement openVolume) ? openVolume.ToString() : "",
                        line.GetProperty("side").ToString() };
                    foreach(object value in values) row.Cells.Add(new Forms.DataGridViewTextBoxCell { Value=value });
                    row.Cells.Add(new Forms.DataGridViewCheckBoxCell { Value=false, ToolTipText="VPS: удаление выбранных уровней пока не подключено" });
                    if (openVolume.ValueKind == JsonValueKind.Number && openVolume.GetDecimal() > 0) row.Cells[5].Style.ForeColor=System.Drawing.Color.Green;
                    _lines.Rows.Add(row);
                }
                UpdateEnabled();
            } finally { _loading=false; }
        }
        private void UpdateEnabled()
        {
            foreach(Field f in _fields) f.Control.IsEnabled = !_busy && (f.Key=="grid_type" ? !_number.HasValue : f.Section=="creator" ? !_number.HasValue || _regime=="Off" : _number.HasValue);
            ButtonCreateGrid.IsEnabled=!_busy && !_number.HasValue;
            ButtonCreateGrid.ToolTip = _number.HasValue ? "VPS: перестроение существующей сетки пока не подключено" : null;
            ToolTipService.SetShowOnDisabled(ButtonCreateGrid, true);
            ButtonStart.IsEnabled=ButtonStop.IsEnabled=ButtonClose.IsEnabled=!_busy && _number.HasValue;
        }
        private void ShowError(Exception ex) { System.Windows.MessageBox.Show(ex.Message,Title,MessageBoxButton.OK,MessageBoxImage.Error); }
    }
}
